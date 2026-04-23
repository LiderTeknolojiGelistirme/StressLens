# StressLens Demo Veri Analiz Raporu

**Tarih:** 2026-04-21
**Analiz:** 8 katilimci demo kaydi, VREED egitim verisi, ML pipeline, Unity scriptleri
**Arac:** Python (pandas, scipy, numpy, scikit-learn), ham byte analizi

---

## 1. Yonetici Ozeti

StressLens, VR ortaminda goz takibi (HTC Vive Focus Vision) ve EDA sensoru (Plux OpenBAN) verilerini kullanarak gercek zamanli stres tespiti yapan bir sistemdir. Model, VREED datasetinde egitilmis bir Stacking Ensemble'dir (LightGBM + LogisticRegression + SVM). 16 Nisan 2026'da 8 katilimciyla demo testi gerceklestirilmistir.

### Temel Bulgular

| Metrik | Deger |
|--------|-------|
| Katilimci sayisi | 8 |
| Toplam tahmin | 1080 |
| Ortalama stres olasiligi | %18.4 |
| Maksimum stres olasiligi | %45.6 |
| Model threshold | %77.7 |
| Stres tespit orani | %0.0 |

**Sonuc:** Model hicbir katilimcida stres tespit edemedi. Bunun 3 temel nedeni vardir: (1) C# kodundaki hesaplama uyumsuzluklari, (2) VREED lab ortami ile VR headset ortami arasindaki domain farki, (3) modelin threshold'unun VR ortami icin cok yuksek olmasi.

---

## 2. Neyi Iyi Yapiyoruz

### 2.1 Model Mimarisi
Stacking Ensemble (LightGBM + LogReg + SVM) guclu bir secim. Tek modele kiyasla daha robust tahminler uretiyor. Base model cesitliligi (tree-based + linear + kernel) ensemble'in farkli veri oruntulerini yakalamasini sagliyor.

### 2.2 Feature Engineering Pipeline
- Yuksek korelasyon filtresi (r > 0.8) ile redundant feature'lar elenmis
- Mutual Information bazli secim ile bilgisiz feature'lar cikarilmis
- Log donusum ile asiri carpik dagilimlari duzeltilmis
- Data leakage onlenmis: tum donusumler sadece train seti uzerinden ogreniyor

### 2.3 SMOTE Uygulama Sirasi
SMOTE sadece train setine uygulanmis, test seti etkilenmemis. Bu dogru bir yaklasim. Sinif dengesizligi (%75 normal / %25 stres) sentitetik orneklerle giderilmis.

### 2.4 Kisiye Ozel Kalibrasyon Sistemi
WebSocket sunucusunda per-client baseline kalibrasyon sistemi tasarlanmis. Her kullanicinin sakin hal baseline'ini ilk 15 saniyede olcup dinamik threshold hesapliyor. Bu, bireysel farkliliklari ele alan iyi bir yaklasim.

### 2.5 Biyolojik Dogrulama
EyeTrackingDataCollector.cs'de 3 katmanli dogrulama:
- Main Sequence (hiz-genlik korelasyonu): Goz hareketi verilerinin gercek insan verisi oldugunu dogruluyor
- Engbert-Kliegl esikleri: Mikrosakkad hizlarinin biyolojik araliklarda oldugunu kontrol ediyor
- Pisagor H/V saglamasi: Yatay ve dikey bilesenlerin tutarli oldugunu test ediyor

### 2.6 GC Optimizasyonu
Unity tarafinda StringBuilder pooling, struct kullanimi (EyeTrackingSample) ve buffer reuse ile real-time performans optimize edilmis. VR ortaminda frame drop'lari minimize eden onemli bir muhendislik karari.

### 2.7 Kapsamli Veri Loglama
DataLogger 4 katmanli CSV kaydi yapiyor:
- eye_tracking_raw.csv: Ham goz takibi verileri (49 feature)
- eda_raw.csv: Ham EDA verileri (8 feature)
- model_features.csv: Modele giden islenenmis feature'lar (32 feature)
- predictions.csv: Model tahmin sonuclari

Bu, post-hoc analiz ve debugging icin son derece degerli.

### 2.8 Saccade Direction Hesaplamasi
C# kodunda saccade yonu radyan, microsaccade yonu derece cinsinden hesaplaniyor. Bu VREED eglitim verisiyle birebir uyumlu:
- Mean_Saccade_Direction: VREED=-0.134 rad, Demo=-0.134 rad (MUKEMMEL)
- Max_Saccade_Direction: VREED=3.114 rad, Demo=3.100 rad (MUKEMMEL)
- Max_Microsac_Dir: VREED=179.69 deg, Demo=173.03 deg (IYI)

---

## 3. Neyi Yanlis veya Eksik Yapiyoruz

### 3.1 KRITIK: Turkce Locale CSV Formatlama Hatasi

**Etki seviyesi:** TUM CSV dosyalari bozuk

C# kodu `ToString("F4")` kullanarak ondalik sayilari yaziyor. Turkce locale'de ondalik ayirici VIRGUL:

```
0.0604 --> "0,0604" --> CSV'de 2 sutuna bolunur: "0" ve "0604"
```

**Kanitlar (ham byte analizi):**
- model_features.csv: Header 34 sutun, veri satirinda 67 alan (her ondalik sayi 2'ye bolunuyor)
- predictions.csv: Header 6 sutun, veri satirinda 9 alan
- VREED egitim verisi: ETKILENMEMIS (Python ile yazilmis, virgul sayilari esit)

**Sonuc:** Post-hoc analiz tamamen guvenilmez. Ancak model inference pipeline'i (WebSocket JSON) muhtemelen dogru calisiyor cunku duzeltilmis prediction olasikliklari toplamda 1.0 veriyor.

### 3.2 KRITIK: microsaccadeMaxAmplitude = 1.0 derece

EyeTrackingDataCollector.cs satirda 31:
```csharp
public float microsaccadeMaxAmplitude = 1.0f;
```

VREED datasetinde "microsaccade" amplitudleri:
- Mean: 11.66 derece, Max: 29.16 derece, SD: 4.51 derece

Unity kodu gercek microsaccade tanimi kullaniyor (<1 derece). VREED ise "kucuk saccade" kategorisini de microsaccade olarak etiketlemis (15 dereceye kadar).

**Etkilenen feature'lar (5 adet):**

| Feature | VREED | Demo | Oran |
|---------|-------|------|------|
| SD_Microsac_V_Amp | 6.25 deg | 0.26 deg | 0.04x |
| Max_Microsac_V_Amp | 34.49 deg | 0.95 deg | 0.03x |
| Mean_Microsac_H_Amp | 0.07 deg | 0.69 deg | 9.4x |
| Skew_Microsac_V_Amp | 0.02 | -0.14 | isaret farki |
| Max_Microsac_Dir | 179.69 deg | 173.03 deg | 0.96x |

### 3.3 KRITIK: H/V Amplitud Isaret Kaybi

`CalculateHorizontalAmplitude()` Acos kullanarak her zaman pozitif deger donduruyor:
```csharp
return Mathf.Acos(dot) * Mathf.Rad2Deg;  // 0-180 arasi, isaret yok
```

VREED verisinde Mean_Microsac_H_Amp: -7.44 ile +5.08 arasi (isaret korunmus, sag/sol bilgisi mevcut).
Demo verisinde: 0.00 ile +1.84 arasi (sadece pozitif, yon bilgisi kayip).

Ayni sorun `CalculateVerticalAmplitude()` icin de gecerli:
```csharp
return Mathf.Abs(pitch2 - pitch1);  // Abs ile isaret yok
```

### 3.4 YUKSEK: Skew_Saccade_Direction Eksik Feature

Model 16 feature bekliyor. EyeTrackingFeatures.ToDictionary() sadece 15 gonderiyor. `Skew_Saccade_Direction` listede yok. WebSocket sunucusu bu feature'i 0 ile dolduruyor.

VREED'de Skew_Saccade_Direction ortalama = 0.058 (radyan). Sifir ile doldurulmas modelin kararini etkiliyor.

### 3.5 YUKSEK: Kalibrasyon Data Leakage

save_model.py satir 242-248:
```python
calibrated_model = CalibratedClassifierCV(stacking_model, method='sigmoid', cv='prefit')
calibrated_model.fit(X_test, y_test)  # TEST VERISI ILE KALIBRASYON!
```

Test seti kalibrasyon parametrelerini ogreniyor, sonra ayni test seti ile performans degerlendiriliyor. Bu, rapor edilen metriklerin sistirilmis olabilecegi anlamina gelir.

### 3.6 ORTA: Model Threshold Cok Yuksek

| Parametre | Deger |
|-----------|-------|
| Optimal threshold (egitim) | 0.7772 |
| Demo max stres olasiligi | 0.4557 |
| Fark | 0.3215 |

Hicbir katilimci threshold'u asamiyor. Bu, egitim ve canli ortam arasindaki dogal farkliliga isaret ediyor.

### 3.7 ORTA: Domain Gap (Ortam Farki)

VREED lab ortami ile VR headset ortami arasindaki temel farklar:

| Parametre | VREED (Lab) | Demo (VR) | Oran |
|-----------|-------------|-----------|------|
| Saccade amplitude | 25.9 deg | 2.3 deg | 0.09x |
| Saccade duration | 364 ms | 23 ms | 0.06x |
| Fixation duration | 2665 ms | 499 ms | 0.19x |
| Blink rate | 0.128/s | 0.021/s | 0.16x |
| EDA Mean | 10.43 uS | 4.25 uS | 0.41x |
| EDA SD | 0.636 uS | 0.176 uS | 0.28x |

**Neden:** VREED laboratuvar ortaminda, yuksek cozunurluklu masaustu eye tracker ile, pasif 360 derece video izleme sirasinda toplanmis. Demo verisi HTC Vive Focus Vision gomulu eye tracker ile, interaktif VR ortaminda toplanmis.

### 3.8 DUSUK: Veri Boyutu vs Model Karmasikligi

275 satir / 16 feature ile Stacking(LightGBM+LogReg+SVM) + SMOTE cok agresif bir setup. Overfitting riski yuksek, ozellikle SMOTE sentetik ornekleri gercek varyasyonu yakalayamayabilir.

---

## 4. Bu Verinin Degeri

### 4.1 Demo Verisinin Neden Degerli Oldugu

Demo kayitlari, modelin GERCEK DUNYA performansini gosteren ilk ve su an icin tek veri kaynagidir. Bu verinin degeri:

**a) Domain Gap Olcumu:**
Hangi feature'larin VR ortaminda hala anlamli oldugunun ortaya koyan tek kaynaktir. Analiz sonucunda:
- YON (direction) feature'lari (radyan/derece) cihazdan bagimsiz, guvenilir
- GENLIK (amplitude) feature'lari cihaz cozunurlugune bagli, buyuk sapma
- ISTATISTIK (skewness, SD) feature'lari ortam dinamigine bagli, orta sapma

**b) Pipeline Dogrulama:**
C# kodundaki hesaplama hatalarini (locale, isaret kaybi, eksik feature) ortaya cikaran tek kaynaktir. Bu bulgular olmadan model "calismiyor" sorununu teshis etmek mumkun olmazdi.

**c) Baseline Olusturma:**
8 katilimcidan toplanan veriler, VR ortamindaki "normal" goz hareketi ve EDA baseline'ini olusturuyor. Gelecekte toplanacak stresli verilerin karsilastirma referansi olarak kullanilabilir.

**d) Feature Range Haritalama:**
VR ortamindaki feature degerlerinin gercek araligini (range) gosteriyor. Bu bilgi:
- Yeni threshold belirleme
- Feature normalizasyon parametreleri
- Outlier detection esilkleri icin gerekli

### 4.2 Hangi Alanda Kullanilabilir

| Kullanim | Aciklama |
|----------|----------|
| Bug fix dogrulama | Locale ve hesaplama duzeltmeleri sonrasi yeni demo ile karsilastirma |
| Parametre kalibrasyonu | fixationVelocityThreshold, microsaccadeMaxAmplitude VR tuning |
| Feature secimi | VR'da bilgisiz feature'lari elemek icin MI/SHAP analizi |
| Threshold belirleme | VR ortamina ozgu stres/normal karar siniri |
| Kullanici profilleme | Kisiler arasi goz hareketi farkliliklari |

---

## 5. Demo Verisini Modelin Egitiminde Kullanma Yol Haritasi

### Asama 1: Veri Temizleme (On Kosul)

Demo verisini kullanmadan once MUTLAKA yapilmasi gerekenler:

**1.1 Locale Duzeltmesi**
Tum C# scriptlerindeki `ToString("F4")` cagrilarina `CultureInfo.InvariantCulture` eklenmeli:
```csharp
using System.Globalization;
value.ToString("F4", CultureInfo.InvariantCulture)
```

Etkilenen dosyalar:
- Assets/Scripts/DataLogger.cs (tum StringBuilder satirlari)
- Assets/Scripts/Unity_Example.cs (DictionaryToJson metodu)

**1.2 Hesaplama Duzeltmeleri**
EyeTrackingDataCollector.cs'de:
```csharp
// microsaccadeMaxAmplitude: 1.0f --> 15.0f
public float microsaccadeMaxAmplitude = 15.0f;

// CalculateHorizontalAmplitude: isareti koru
float hDiff = gazeDir2.x - gazeDir1.x;
float angleDeg = Mathf.Acos(dot) * Mathf.Rad2Deg;
return hDiff >= 0 ? angleDeg : -angleDeg;

// CalculateVerticalAmplitude: Abs kaldir
return pitch2 - pitch1;
```

EyeTrackingFeatures.ToDictionary()'ye ekle:
```csharp
{ "Skew_Saccade_Direction", Skew_Saccade_Direction }
```

**1.3 Duzeltilmis Demo Toplama**
Locale ve hesaplama duzeltmeleri sonrasi AYNI 8 katilimci veya yeni katilimcilarla demo tekrarla. Bu, temiz baseline verisi olusturur.

### Asama 2: Etiketleme Stratejisi

Demo verisini egitimde kullanmak icin GROUND TRUTH etiketleri gerekiyor. Mevcut demoda etiket yok.

**Secenek A: Self-Report Etiketleme (Onerilen)**
- VR oturumu sirasinda veya sonrasinda katilimciya stres durumunu sor
- SAM (Self-Assessment Manikin) veya VAS (Visual Analog Scale) kullan
- Her 30sn'lik pencere icin bir etiket al (timeWindow ile uyumlu)
- Avantaj: Basit, hizli, maliyet dusuk
- Dezavantaj: Subjektif, gecikme olabilir

**Secenek B: Kontollu Stres Senaryolari (Ideal)**
- VR ortaminda stresli gorevler tasarla:
  - Stroop task (renk-kelime uyumsuzlugu)
  - Mental aritmetik (geri sayma)
  - Zaman baskisi altinda gorev tamamlama
  - Ani uyaranlar (ses, gorsel)
- Sakin donemler ile stresli donemleri alternatif yap
- Her donem otomatik etiketlenir (senaryo bilinen)
- Avantaj: Objektif, tekrarlanabilir
- Dezavantaj: Senaryo gelistirme zamani

**Secenek C: Fizyolojik Esik Etiketleme (Ek)**
- EDA phasic yanitlari (SCR) etiket olarak kullan
- Kalp hizi degiskenligini (HRV) ek sensorden al
- Avantaj: Objektif fizyolojik isaret
- Dezavantaj: Ek sensör gerektirir

### Asama 3: Veri Birlestirme Stratejisi

VREED (312 satir) ve VR demo verisini birlestirmenin 3 yolu:

**Strateji 1: Karistirma (Pooling) - Basit**
```python
X_combined = pd.concat([X_vreed, X_vr], ignore_index=True)
y_combined = pd.concat([y_vreed, y_vr], ignore_index=True)
model.fit(X_combined, y_combined)
```
- Avantaj: En fazla veri, basit
- Dezavantaj: Domain farkini gormezden gelir
- Ne zaman: Feature olcekleri duzeltildikten SONRA

**Strateji 2: Fine-Tuning - Onerilen**
```python
# 1. VREED ile pre-train
model.fit(X_vreed, y_vreed)

# 2. VR verisi ile fine-tune (transfer learning)
model.fit(X_vr, y_vr, init_model=pretrained_model)
```
- Avantaj: VREED'in genel oruntulerini korur, VR'ya adapte olur
- Dezavantaj: LightGBM'de init_model destegi gerekir
- Ne zaman: Yeterli VR verisi toplandiktan sonra (50+ ornek)

**Strateji 3: Domain Adaptation - Ileri Seviye**
```python
# Feature normalizasyon: Her domain icin Z-score
X_vreed_norm = (X_vreed - X_vreed.mean()) / X_vreed.std()
X_vr_norm = (X_vr - X_vr.mean()) / X_vr.std()
X_combined = pd.concat([X_vreed_norm, X_vr_norm])
```
- Avantaj: Olcek farklarini giderir
- Dezavantaj: Domain-specific bilgi kaybedilebilir
- Ne zaman: Feature olcekleri cok farkli oldugunda (mevcut durum)

**Onerilen Yol:** Oncelikle Asama 1 duzeltmelerini yap, sonra Strateji 3 (Z-score normalizasyon) ile baslayip, yeterli VR verisi toplandikca Strateji 2'ye (fine-tuning) gec.

### Asama 4: Feature Yeniden Degerlendirme

VR verisi toplandiktan sonra feature importance'i yeniden hesapla:

```python
from sklearn.feature_selection import mutual_info_classif

mi_vreed = mutual_info_classif(X_vreed, y_vreed)
mi_vr = mutual_info_classif(X_vr, y_vr)

# Karsilastir: Hangi feature'lar VR'da daha bilgilendirici?
```

Beklenen sonuclar:
- Direction feature'lari (radyan): Her iki ortamda da bilgilendirici
- Amplitude feature'lari: VREED'de yuksek MI, VR'da dusuk MI (olcek farki)
- Blink feature'lari: VR'da farkli davranis (headset etkisi)
- EDA feature'lari: Daha uzun oturumlarda daha bilgilendirici

### Asama 5: Model Yeniden Egitim

**Onerilen model yaklasimi:**

```python
import lightgbm as lgb

# Basit ve stabil: Tek LightGBM + class_weight
model = lgb.LGBMClassifier(
    n_estimators=200,
    learning_rate=0.01,
    max_depth=4,
    num_leaves=15,
    subsample=0.7,
    colsample_bytree=0.7,
    class_weight='balanced',  # SMOTE yerine
    random_state=42
)

# VR-specific threshold: Precision-Recall egrisinden
from sklearn.metrics import precision_recall_curve
precisions, recalls, thresholds = precision_recall_curve(y_test, y_proba)
f1_scores = 2 * (precisions * recalls) / (precisions + recalls + 1e-8)
optimal_threshold = thresholds[np.argmax(f1_scores)]
```

Neden Stacking yerine tek LightGBM:
- 275+N satir icin stacking asiri karmasik
- Tek model daha yorumlanabilir (SHAP degerleri)
- Daha hizli egitim ve inference
- class_weight='balanced' SMOTE kadar etkili, daha stabil

### Asama 6: Kalibrasyon ve Threshold

**Kalibrasyon leakage duzeltmesi:**
```python
from sklearn.model_selection import train_test_split

# 3'e bol: train / calibration / test
X_train, X_temp, y_train, y_temp = train_test_split(X, y, test_size=0.3)
X_cal, X_test, y_cal, y_test = train_test_split(X_temp, y_temp, test_size=0.5)

# Model egit
model.fit(X_train, y_train)

# Kalibrasyon (ayri set ile)
from sklearn.calibration import CalibratedClassifierCV
calibrated = CalibratedClassifierCV(model, cv='prefit')
calibrated.fit(X_cal, y_cal)

# Performans degerlendirme (bagimsiz test seti)
y_proba = calibrated.predict_proba(X_test)[:, 1]
```

**VR-specific threshold:**
```python
# websocket_server.py
STRESS_OFFSET = 0.10   # 0.15'ten dusur
MIN_THRESHOLD = 0.15   # 0.25'ten dusur
MAX_THRESHOLD = 0.50   # 0.60'tan dusur
DEFAULT_THRESHOLD = 0.40  # 0.7772'den dusur (VR icin)
```

---

## 6. Katilimci Bazinda Sonuclar

| Katilimci | Tahmin | Ort. Stres Prob | Min | Max | Stres% |
|-----------|--------|-----------------|-----|-----|--------|
| AhmetAlperenPolat | 133 | 0.0946 | 0.0510 | 0.3392 | 0.0% |
| AliErenAcar | 117 | 0.2273 | 0.0858 | 0.4554 | 0.0% |
| BerkayUlgen | 133 | 0.1928 | 0.1041 | 0.4203 | 0.0% |
| CemSuha | 140 | 0.2147 | 0.0696 | 0.4557 | 0.0% |
| CeydaSaka | 156 | 0.2723 | 0.0938 | 0.4116 | 0.0% |
| ElifYildirim | 114 | 0.1025 | 0.0895 | 0.1893 | 0.0% |
| MehmetAliBasli | 121 | 0.1533 | 0.0968 | 0.3506 | 0.0% |
| MehmetArikan | 166 | 0.1883 | 0.0872 | 0.4000 | 0.0% |
| **TOPLAM** | **1080** | **0.1841** | **0.0510** | **0.4557** | **0.0%** |

Not: Stres olasiliklari locale-duzeltilmis ham byte analizinden elde edilmistir. Threshold: 0.7772.

---

## 7. 16 Model Feature Karsilastirma Tablosu

| # | Feature | VREED Mean | Demo Mean | Oran | Log? | Durum |
|---|---------|------------|-----------|------|------|-------|
| 1 | Number of Peaks | 0.0000 | 0.0000 | 0.70x | | OK |
| 2 | SD_Saccade_Direction | 2.10 | 1.80 | 0.85x | | OK |
| 3 | Mean_Blink_Duration | 4.81 (log) | 1.52 (log) | 0.32x | LOG | Orta sapma |
| 4 | Num_of_Blink | 0.13 (log) | 0.02 (log) | 0.15x | LOG | Buyuk sapma |
| 5 | Skew_Microsac_V_Amp | -0.08 | -0.08 | ~1x | | OK |
| 6 | Max_Microsac_Dir | 5.20 (log) | 5.18 (log) | ~1x | LOG | OK |
| 7 | Max_Saccade_Direction | 1.41 (log) | 1.42 (log) | ~1x | LOG | OK |
| 8 | Mean_Microsac_H_Amp | 0.09 | 0.70 | 9.4x | | Buyuk sapma |
| 9 | Num_of_Fixations | 0.40 | 1.16 | 2.9x | | Orta sapma |
| 10 | Mean_Saccade_Direction | -0.13 | -0.12 | ~1x | | OK |
| 11 | SD_Microsac_V_Amp | 6.39 | 0.27 | 0.04x | | Buyuk sapma |
| 12 | Skew_Saccade_Direction | 0.06 | 0.00 | 0.00x | | EKSIK |
| 13 | Mean_Saccade_Duration | 368 ms | 23 ms | 0.06x | | Buyuk sapma |
| 14 | SD (GSR) | 0.46 (log) | 0.12 (log) | 0.27x | LOG | Orta sapma |
| 15 | Max_Microsac_V_Amp | 34.37 | 0.96 | 0.03x | | Buyuk sapma |
| 16 | Skew_Fixation_Duration | 2.26 | 2.07 | 0.92x | | OK |

**Ozet:** 16 feature'dan 7'si iyi eslesiyor (OK), 3'u orta sapma gosteriyor, 5'i buyuk sapma gosteriyor ve 1'i eksik.

---

## 8. Onceliklendirilmis Eylem Plani

### Asama 1 - HEMEN (2-3 saat)
1. DataLogger.cs: Tum ToString cagirlarina InvariantCulture ekle
2. Unity_Example.cs: DictionaryToJson'da InvariantCulture kullan
3. EyeTrackingFeatures.ToDictionary: Skew_Saccade_Direction ekle
4. EyeTrackingDataCollector.cs: microsaccadeMaxAmplitude = 15.0f
5. CalculateHorizontalAmplitude: Yon isaretini koru
6. CalculateVerticalAmplitude: Abs kaldir

### Asama 2 - KISA VADE (1 gun)
1. Duzeltilmis kod ile yeni demo kayitlari topla
2. Bu rapordaki analizi tekrarla (dogrulama)
3. websocket_server.py threshold parametrelerini ayarla
4. GSRDataCollector: waveAmplitudeThreshold=0.1, minimumEDABaseline=0.1

### Asama 3 - ORTA VADE (1 hafta)
1. save_model.py kalibrasyon leakage duzelt
2. Etiketli VR verisi topla (kontollu stres senaryolari)
3. VREED + VR karma dataset olustur
4. Tek LightGBM + class_weight='balanced' ile yeniden egit

### Asama 4 - UZUN VADE (2+ hafta)
1. Domain adaptation teknikleri uygula
2. VR-specific feature secimi (SHAP analizi)
3. Kisisellestirilmis model (per-user fine-tuning)
4. Klinik dogrulama calismalari

---

## 9. Teknik Ekler

### Ek A: CSV Locale Hatasi Kaniti

predictions.csv ham byte icerigi:
```
Header virgul sayisi: 5 (6 sutun)
Veri virgul sayisi:   8 (9 alan)
Fark: 3 fazla virgul
```

Ornek satir:
```
2026-04-16 15:08:34.481,9,985,0,0,0604,0,9396,normal
```

Duzeltilmis okuma:
```
SessionTime: 9.985
Stress: 0
Stress_Probability: 0.0604
NoStress_Probability: 0.9396
Status: normal
```

### Ek B: VREED vs Demo Ortam Farklari

```
Parametre              VREED (Lab)              Demo (VR Headset)
Eye tracker            Masaustu (yuksek coz.)   HTC Vive Focus gomulu
Spatial resolution     ~0.5 derece veya alti    ~1-2 derece (VR gozluk)
Uyaran                 360 derece pasif video   Interaktif VR ortam
Bas hareketi           Serbest (sandalye)       VR headset ile
Kayit suresi           1-3 dakika video         Degisken oturum
Sampling rate          Muhtemelen 60-120Hz      100Hz (ayarlanmis)
```

### Ek C: Modelin Gordugu Feature Araliklari (Log Donusum Sonrasi)

```
Feature                        Mean        Std        Min        Max
Number of Peaks               0.0000     0.0000     0.0000     0.0001
SD_Saccade_Direction          2.1014     0.1690     1.5723     2.8678
Mean_Blink_Duration           4.8088     0.4678     4.0547     6.6050
Num_of_Blink                  0.1307     0.1226     0.0110     0.7577
Skew_Microsac_V_Amp          -0.0801     2.7261    -9.0050     9.0956
Max_Microsac_Dir              5.1967     0.0029     5.1794     5.1985
Max_Saccade_Direction         1.4107     0.0883     0.1224     1.4211
Mean_Microsac_H_Amp           0.0889     1.3608    -7.4429     5.0766
Num_of_Fixations              0.3999     0.1262     0.0222     0.6897
Mean_Saccade_Direction       -0.1333     0.3822    -1.7617     1.1987
SD_Microsac_V_Amp             6.3874     3.5491     0.7813    18.6868
Skew_Saccade_Direction        0.0587     0.1777    -0.6964     1.1374
Mean_Saccade_Duration       368.1826   160.7029    63.6000  1013.7013
SD                            0.4585     0.2721     0.0148     1.5999
Max_Microsac_V_Amp           34.3656    22.7207     2.2570   110.5797
Skew_Fixation_Duration        2.2556     0.9353    -0.1230     5.7999
```

---

*Bu rapor, 4-ajan ML analiz sistemi (Veri Kesfi, Data Drift, Pipeline Denetimi, Sentez) tarafindan uretilmis ve konsolide edilmistir.*
