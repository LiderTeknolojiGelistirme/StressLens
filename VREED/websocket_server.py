"""
LightGBM modelini kullanan WebSocket sunucusu.
Unity'den gelen verileri alır, tahmin yapar ve sonucu geri gönderir.

KİŞİYE ÖZEL KALİBRASYON SİSTEMİ:
- Session başında kalibrasyon dönemi (varsayılan: ilk 5 tahmin)
- Kişisel baseline hesaplama (sakin haldeki ortalama stres olasılığı)
- Dinamik threshold: baseline + offset
"""
import asyncio
import websockets
import json
import pickle
import numpy as np
import pandas as pd
from pathlib import Path
import logging
from collections import deque

# Logging ayarları
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

# ========== KALİBRASYON AYARLARI ==========
CALIBRATION_DURATION_SECONDS = 15  # Kalibrasyon süresi (saniye)
STRESS_OFFSET = 0.15  # Baseline üzerine eklenecek offset (örn: %15)
MIN_THRESHOLD = 0.25  # Minimum threshold (baseline çok düşük olsa bile)
MAX_THRESHOLD = 0.60  # Maximum threshold (baseline çok yüksek olsa bile)
DEFAULT_THRESHOLD = 0.50  # Kalibrasyon tamamlanmadan kullanılacak threshold (model_meta'dan güncellenir)

# ========== KALİBRASYON DURUMU (per-client) ==========
class CalibrationState:
    """Her client için kalibrasyon durumunu tutar"""
    def __init__(self):
        self.calibration_values = []  # Kalibrasyon dönemindeki stres olasılıkları
        self.calibration_start_time = None  # Kalibrasyon başlangıç zamanı
        self.is_calibrated = False
        self.baseline = 0.0
        self.threshold = DEFAULT_THRESHOLD
        self.prediction_count = 0
    
    def add_calibration_value(self, stress_prob):
        """Kalibrasyon değeri ekle (zaman bazlı)"""
        import time
        
        # İlk tahmin - kalibrasyon zamanını başlat
        if self.calibration_start_time is None:
            self.calibration_start_time = time.time()
            logger.info(f"⏱️ Kalibrasyon başladı ({CALIBRATION_DURATION_SECONDS} saniye)")
        
        elapsed = time.time() - self.calibration_start_time
        remaining = max(0, CALIBRATION_DURATION_SECONDS - elapsed)
        
        if elapsed < CALIBRATION_DURATION_SECONDS:
            self.calibration_values.append(stress_prob)
            logger.info(f"📊 Kalibrasyon: {elapsed:.1f}s / {CALIBRATION_DURATION_SECONDS}s - Stres: {stress_prob:.2%} (Kalan: {remaining:.1f}s)")
        else:
            # Süre doldu, kalibrasyonu tamamla
            if not self.is_calibrated:
                self._complete_calibration()
    
    def _complete_calibration(self):
        """Kalibrasyonu tamamla ve threshold hesapla"""
        if len(self.calibration_values) == 0:
            logger.warning("⚠️ Kalibrasyon verisi yok, varsayılan threshold kullanılacak")
            self.baseline = 0.3
            self.threshold = DEFAULT_THRESHOLD
        else:
            self.baseline = np.mean(self.calibration_values)
            self.threshold = self.baseline + STRESS_OFFSET
        
        # Threshold'u sınırla
        self.threshold = max(MIN_THRESHOLD, min(MAX_THRESHOLD, self.threshold))
        self.is_calibrated = True
        
        logger.info("=" * 50)
        logger.info("✅ KALİBRASYON TAMAMLANDI!")
        logger.info(f"   Toplanan örnek: {len(self.calibration_values)} tahmin")
        logger.info(f"   Baseline (sakin hal): {self.baseline:.2%}")
        logger.info(f"   Offset: {STRESS_OFFSET:.2%}")
        logger.info(f"   Dinamik Threshold: {self.threshold:.2%}")
        logger.info("=" * 50)
    
    def get_stress_decision(self, stress_prob):
        """Dinamik threshold'a göre stres kararı ver"""
        self.prediction_count += 1
        
        if not self.is_calibrated:
            self.add_calibration_value(stress_prob)
            # Kalibrasyon döneminde varsayılan threshold kullan
            return stress_prob >= DEFAULT_THRESHOLD
        
        return stress_prob >= self.threshold
    
    def get_status_info(self):
        """Kalibrasyon durumu bilgisi"""
        import time
        
        if not self.is_calibrated:
            elapsed = 0
            if self.calibration_start_time is not None:
                elapsed = time.time() - self.calibration_start_time
            
            return {
                "calibration_progress": min(elapsed, CALIBRATION_DURATION_SECONDS),
                "calibration_total": CALIBRATION_DURATION_SECONDS,
                "is_calibrated": False,
                "threshold": DEFAULT_THRESHOLD
            }
        return {
            "calibration_progress": CALIBRATION_DURATION_SECONDS,
            "calibration_total": CALIBRATION_DURATION_SECONDS,
            "is_calibrated": True,
            "baseline": self.baseline,
            "threshold": self.threshold
        }

# Client bazlı kalibrasyon durumları
client_states = {}

# Model ve özellik dosya yolları
MODEL_DIR = Path('model')
MODEL_PATH = MODEL_DIR / 'lightgbm_model.pkl'
FEATURES_PATH = MODEL_DIR / 'feature_columns.json'
META_PATH = MODEL_DIR / 'model_meta.json'

# Model, özellik listesi ve meta verileri yükle
try:
    logger.info("Model yükleniyor...")
    with open(MODEL_PATH, 'rb') as f:
        model = pickle.load(f)
    logger.info(f"✓ Model yüklendi: {MODEL_PATH}")

    with open(FEATURES_PATH, 'r', encoding='utf-8') as f:
        feature_columns = json.load(f)
    logger.info(f"✓ Özellik listesi yüklendi: {FEATURES_PATH}")
    logger.info(f"✓ Beklenen özellik sayısı: {len(feature_columns)}")

    # Meta veriler (log dönüşüm yapılacak özellikler, optimal threshold)
    model_meta = {}
    if META_PATH.exists():
        with open(META_PATH, 'r', encoding='utf-8') as f:
            model_meta = json.load(f)
        logger.info(f"✓ Model meta verileri yüklendi")
        logger.info(f"  Optimal threshold: {model_meta.get('optimal_threshold', 'yok')}")
        logger.info(f"  Log dönüşüm: {len(model_meta.get('log_transformed_features', []))} özellik")

    # Optimal threshold'u meta'dan yükle
    if 'optimal_threshold' in model_meta:
        DEFAULT_THRESHOLD = model_meta['optimal_threshold']
        logger.info(f"✓ Default threshold güncellendi: {DEFAULT_THRESHOLD:.3f}")

except FileNotFoundError as e:
    logger.error(f"Model veya özellik dosyası bulunamadı: {e}")
    logger.error("Önce 'python save_model.py' komutunu çalıştırın!")
    exit(1)
except Exception as e:
    logger.error(f"Model yükleme hatası: {e}")
    exit(1)

def validate_input(data):
    """
    Unity'den gelen veriyi doğrula ve düzenle.
    Unity'den gönderilen tüm özellikler feature_columns.json dosyasına göre kontrol edilir.
    
    Args:
        data: Unity'den gelen JSON dict
        
    Returns:
        pandas.DataFrame: Model için hazır veri
    """
    # Veriyi DataFrame'e çevir
    input_data = pd.DataFrame([data])
    
    # Gelen özellikleri logla
    received_features = list(input_data.columns)
    logger.debug(f"Gelen özellik sayısı: {len(received_features)}")
    
    # Fazla özellikleri kontrol et (modelde kullanılmayan özellikler)
    extra_features = [col for col in received_features if col not in feature_columns]
    if extra_features:
        logger.debug(f"Modelde kullanılmayan fazla özellikler ({len(extra_features)} adet): {extra_features[:5]}...")
    
    # Eksik özellikleri kontrol et ve ekle
    missing_features = []
    for col in feature_columns:
        if col not in input_data.columns:
            input_data[col] = np.nan
            missing_features.append(col)
    
    if missing_features:
        logger.warning(f"Eksik özellikler bulundu ({len(missing_features)} adet): {missing_features[:5]}...")
        # Eksik özellikleri 0 ile doldur
        input_data[missing_features] = 0
    
    # Özellikleri doğru sıraya koy (modelin beklediği sıraya göre)
    input_data = input_data[feature_columns]

    # NaN kontrolü (güvenlik için)
    if input_data.isnull().any().any():
        nan_cols = input_data.columns[input_data.isnull().any()].tolist()
        logger.warning(f"NaN değerler bulundu, 0 ile dolduruluyor: {nan_cols}")
        input_data = input_data.fillna(0)

    # Veri tipi kontrolü (tüm değerler sayısal olmalı)
    for col in input_data.columns:
        if not pd.api.types.is_numeric_dtype(input_data[col]):
            logger.warning(f"Özellik '{col}' sayısal değil, 0 ile dolduruluyor")
            input_data[col] = pd.to_numeric(input_data[col], errors='coerce').fillna(0)

    # Log dönüşüm (model eğitiminde uygulanan dönüşüm)
    log_features = model_meta.get('log_transformed_features', [])
    for col in log_features:
        if col in input_data.columns:
            input_data[col] = np.sign(input_data[col]) * np.log1p(np.abs(input_data[col]))

    return input_data

def predict_stress(input_data, calibration_state=None):
    """
    Model ile stres tahmini yap.
    
    Args:
        input_data: pandas.DataFrame
        calibration_state: CalibrationState - kişiye özel kalibrasyon durumu
        
    Returns:
        dict: Tahmin sonuçları
    """
    try:
        # Olasılık tahmini yap
        prediction_proba = model.predict_proba(input_data)[0]
        stress_prob = float(prediction_proba[1])
        
        # Kişiye özel kalibrasyon ile karar ver
        if calibration_state:
            is_stressed = calibration_state.get_stress_decision(stress_prob)
            calibration_info = calibration_state.get_status_info()
        else:
            # Kalibrasyon yoksa varsayılan threshold
            is_stressed = stress_prob >= DEFAULT_THRESHOLD
            calibration_info = {"is_calibrated": False, "threshold": DEFAULT_THRESHOLD}
        
        # Sonucu hazırla
        result = {
            "stres": int(is_stressed),  # 0 veya 1
            "stres_olasilik": stress_prob,  # Stres olma olasılığı
            "stres_yok_olasilik": float(prediction_proba[0]),  # Stres yok olasılığı
            "durum": "stresli" if is_stressed else "normal",
            "basari": True,
            # Kalibrasyon bilgileri
            "kalibrasyon": calibration_info
        }
        
        return result
        
    except Exception as e:
        logger.error(f"Tahmin hatası: {e}")
        return {
            "error": f"Tahmin hatası: {str(e)}",
            "basari": False
        }

async def handle_client(websocket, path=None):
    """
    Unity'den gelen WebSocket bağlantısını işle.
    Her client için ayrı kalibrasyon durumu tutulur.
    
    Args:
        websocket: WebSocket bağlantısı
        path: URL path (opsiyonel, farklı WebSocket versiyonları için)
    """
    client_address = websocket.remote_address
    client_id = str(client_address)
    
    # Bu client için yeni kalibrasyon durumu oluştur
    client_states[client_id] = CalibrationState()
    calibration_state = client_states[client_id]
    
    logger.info("=" * 50)
    logger.info(f"🔗 Unity bağlantısı kuruldu: {client_address}")
    logger.info(f"📋 Kalibrasyon başlıyor ({CALIBRATION_DURATION_SECONDS} saniye)")
    logger.info(f"   Sakin bir şekilde bekleyin...")
    logger.info("=" * 50)
    
    try:
        async for message in websocket:
            try:
                # Unity'den gelen JSON'u parse et
                data = json.loads(message)
                logger.info(f"Alınan veri: {len(data)} özellik (Beklenen: {len(feature_columns)} özellik)")
                
                # Veriyi doğrula ve hazırla
                input_data = validate_input(data)
                logger.debug(f"Model için hazırlanan veri: {input_data.shape[1]} özellik")
                
                # Tahmin yap (kişiye özel kalibrasyon ile)
                result = predict_stress(input_data, calibration_state)
                
                # Unity'ye gönder
                response = json.dumps(result, ensure_ascii=False)
                await websocket.send(response)
                if result.get('basari', False):
                    cal_info = result.get('kalibrasyon', {})
                    if cal_info.get('is_calibrated', False):
                        logger.info(f"📊 Tahmin: {result.get('durum', 'bilinmiyor')} | Olasılık: {result.get('stres_olasilik', 0):.1%} | Threshold: {cal_info.get('threshold', 0):.1%}")
                    else:
                        logger.info(f"⏳ Kalibrasyon: {cal_info.get('calibration_progress', 0):.1f}s/{cal_info.get('calibration_total', CALIBRATION_DURATION_SECONDS)}s | Olasılık: {result.get('stres_olasilik', 0):.1%}")
                else:
                    logger.warning(f"Hata yanıtı gönderildi: {result.get('error', 'Bilinmeyen hata')}")
                
            except json.JSONDecodeError as e:
                error_msg = {
                    "error": f"Geçersiz JSON formatı: {str(e)}",
                    "basari": False
                }
                await websocket.send(json.dumps(error_msg, ensure_ascii=False))
                logger.error(f"JSON parse hatası: {e}")
                
            except Exception as e:
                error_msg = {
                    "error": f"Sunucu hatası: {str(e)}",
                    "basari": False
                }
                await websocket.send(json.dumps(error_msg, ensure_ascii=False))
                logger.error(f"Beklenmeyen hata: {e}", exc_info=True)
                
    except websockets.exceptions.ConnectionClosed:
        logger.info(f"🔌 Unity bağlantısı kapatıldı: {client_address}")
    except Exception as e:
        logger.error(f"Bağlantı hatası: {e}", exc_info=True)
    finally:
        # Client state'i temizle
        if client_id in client_states:
            state = client_states[client_id]
            logger.info(f"📈 Session özeti: {state.prediction_count} tahmin yapıldı")
            if state.is_calibrated:
                logger.info(f"   Baseline: {state.baseline:.1%} | Threshold: {state.threshold:.1%}")
            del client_states[client_id]

async def main():
    """
    WebSocket sunucusunu başlat.
    """
    host = "localhost"
    port = 8765
    
    logger.info("=" * 50)
    logger.info("WEBSOCKET SUNUCUSU")
    logger.info("=" * 50)
    logger.info(f"Sunucu adresi: ws://{host}:{port}")
    logger.info("Unity'den bağlantı bekleniyor...")
    logger.info("Çıkmak için Ctrl+C")
    logger.info("=" * 50)
    
    try:
        async with websockets.serve(handle_client, host, port):
            await asyncio.Future()  # Sonsuz döngü
    except KeyboardInterrupt:
        logger.info("\nSunucu kapatılıyor...")
    except Exception as e:
        logger.error(f"Sunucu hatası: {e}", exc_info=True)

if __name__ == "__main__":
    asyncio.run(main())

