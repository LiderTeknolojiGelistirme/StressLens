# LightGBM WebSocket Sunucusu

Unity ile iletişim kuran LightGBM stres tespiti modeli için WebSocket sunucusu.

## Kurulum

### 1. Gerekli Paketleri Yükleyin

```bash
pip install -r requirements_ws.txt
```

### 2. Modeli Kaydedin

Önce notebook'tan eğitilen modeli kaydetmek için:

```bash
python save_model.py
```

Bu komut:
- Veri setlerini yükler
- Veri hazırlama adımlarını uygular
- LightGBM modelini eğitir
- Modeli `model/lightgbm_model.pkl` olarak kaydeder
- Özellik listesini `model/feature_columns.json` olarak kaydeder

### 3. WebSocket Sunucusunu Başlatın

```bash
python websocket_server.py
```

Sunucu `ws://localhost:8765` adresinde çalışacaktır.

## Kullanım

### Unity'den Veri Gönderme Formatı

Unity'den gönderilecek JSON formatı:

```json
{
  "Number of Peaks": 15.0,
  "SD_Saccade_Direction": 15.2,
  "Mean_Blink_Duration": 150.0,
  "Num_of_Blink": 12.5,
  "Skew_Microsac_V_Amp": 0.15,
  "Max_Microsac_Dir": 110.0,
  "Max_Saccade_Direction": 120.0,
  "Mean_Microsac_H_Amp": 1.5,
  "Num_of_Fixations": 45.2,
  "Mean_Saccade_Direction": 90.0,
  "SD_Microsac_V_Amp": 0.8,
  "Skew_Saccade_Direction": 1.2,
  "Mean_Saccade_Duration": 45.3,
  "SD": 1.8,
  "Max_Microsac_V_Amp": 2.5,
  "Skew_Fixation_Duration": 0.8
}
```

**Not:** v3 model 16 özellik kullanır (eski 32 yerine). Ham değerler gönderilmeli — log dönüşüm sunucu tarafında otomatik uygulanır. Eksik özellikler otomatik olarak 0 ile doldurulur.

### Sunucudan Gelen Yanıt Formatı

```json
{
  "stres": 1,
  "stres_olasilik": 0.85,
  "stres_yok_olasilik": 0.15,
  "durum": "stresli",
  "basari": true
}
```

veya hata durumunda:

```json
{
  "error": "Hata mesajı",
  "basari": false
}
```

## Özellik Listesi

v3 model 16 özellik kullanmaktadır (14 Eye Tracking + 2 GSR). Tam liste için `model/feature_columns.json` dosyasına bakın.
Model meta verileri (log dönüşüm listesi, optimal threshold) için `model/model_meta.json` dosyasına bakın.

## Unity C# Örneği

Unity tarafında kullanım için örnek C# kodu:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using NativeWebSocket;

public class StressPredictor : MonoBehaviour
{
    WebSocket websocket;
    public string serverUrl = "ws://localhost:8765";
    
    // Özellik değerlerini buraya koy
    public Dictionary<string, float> features = new Dictionary<string, float>();
    
    async void Start()
    {
        websocket = new WebSocket(serverUrl);
        
        websocket.OnOpen += () => {
            Debug.Log("Python sunucusuna bağlandı!");
        };
        
        websocket.OnError += (e) => {
            Debug.LogError($"WebSocket hatası: {e}");
        };
        
        websocket.OnClose += (e) => {
            Debug.Log("Bağlantı kapatıldı");
        };
        
        websocket.OnMessage += (bytes) => {
            string message = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.Log($"Sunucudan gelen: {message}");
            
            // JSON'u parse et
            var result = JsonUtility.FromJson<StressResult>(message);
            Debug.Log($"Stres Durumu: {result.durum}, Olasılık: {result.stres_olasilik}");
        };
        
        await websocket.Connect();
    }
    
    public async void PredictStress()
    {
        if (websocket.State == WebSocketState.Open)
        {
            // Özellikleri JSON'a çevir
            string json = JsonUtility.ToJson(features);
            await websocket.SendText(json);
        }
    }
    
    void Update()
    {
        #if !UNITY_WEBGL || UNITY_EDITOR
        websocket?.DispatchMessageQueue();
        #endif
    }
    
    async void OnApplicationQuit()
    {
        await websocket.Close();
    }
}

[Serializable]
public class StressResult
{
    public int stres;
    public float stres_olasilik;
    public float stres_yok_olasilik;
    public string durum;
    public bool basari;
}
```

## Sorun Giderme

### Model dosyası bulunamadı
- `python save_model.py` komutunu çalıştırdığınızdan emin olun
- `model/` klasörünün mevcut olduğunu kontrol edin

### Bağlantı hatası
- Sunucunun çalıştığından emin olun (`python websocket_server.py`)
- Port 8765'in kullanılabilir olduğunu kontrol edin
- Firewall ayarlarını kontrol edin

### Tahmin hatası
- Unity'den gönderilen özellik isimlerinin doğru olduğundan emin olun
- Tüm özelliklerin gönderildiğini kontrol edin
- Özellik değerlerinin sayısal olduğundan emin olun

## Port Değiştirme

Portu değiştirmek için `websocket_server.py` dosyasındaki `port = 8765` satırını düzenleyin.

