using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

/// <summary>
/// Plux cihazından GSR (Galvanic Skin Response) verilerini toplayıp işleyen script
/// PluxDeviceManager API kullanır
/// </summary>
public class GSRDataCollector : MonoBehaviour
{
    [Header("GSR/EDA Ayarları")]
    [Tooltip("GSR/EDA verilerini toplamayı başlat")]
    public bool startCollecting = false;
    
    [Tooltip("Veri toplama frekansı (Hz) - OpenBAN için max: 1000Hz, EDA için önerilen: 100-1000Hz")]
    public int samplingRate = 1000;
    
    [Tooltip("EDA sensörü hangi kanala bağlı? (OpenBAN için: 1)")]
    public int gsrChannel = 1;
    
    [Header("Filtreleme Ayarları")]
    [Tooltip("Hareketli ortalama filtresini aktif et - Parazitleri temizlemek için")]
    public bool enableFiltering = true;
    
    [Tooltip("Hareketli ortalama pencere boyutu (örnek sayısı) - Daha büyük değer daha fazla yumuşatma sağlar. Önerilen: 5-20 (1000Hz için)")]
    public int filterWindowSize = 10;
    
    [Header("Hesaplama Parametreleri")]
    [Tooltip("Dalga tespiti için minimum genlik eşiği (µS) - Stres tepkisinin minimum yüksekliği. Önerilen: 0.3-0.5µS")]
    public float waveAmplitudeThreshold = 0.3f;
    
    [Tooltip("Yükselme fazı için minimum örnek sayısı - Stres tepkisinin yükselme süresi. Önerilen: 10-30 örnek (1000Hz için ~0.01-0.03s)")]
    public int minRiseSamples = 15;
    
    [Tooltip("Sönümlenme fazı için minimum örnek sayısı - Stres tepkisinin sönümlenme süresi. Önerilen: 20-50 örnek (1000Hz için ~0.02-0.05s)")]
    public int minDecaySamples = 30;
    
    [Tooltip("Özellik hesaplama için zaman penceresi (saniye) - VREED dataset'ine göre: 30-60 saniye (360-VE videoları 1-3 dakika)")]
    public float timeWindow = 30f;
    
    [Tooltip("Number of Peaks normalizasyonu - VREED dataset'ine göre: peak sayısı / toplam sample sayısı")]
    public bool normalizePeakCount = true;
    
    [Tooltip("Normalizasyon yöntemi: true = sample sayısına böl (training verisiyle uyumlu), false = zaman penceresine böl")]
    public bool normalizeBysamples = true;
    
    [Tooltip("Training verisi tahmini sampling rate (Hz) - VREED dataset muhtemelen 100-120 Hz kullandı. Normalizasyon için gerekli.")]
    public float trainingSamplingRate = 100f;
    
    [Header("EDA Sensör Transfer Fonksiyonu")]
    [Tooltip("ADC çözünürlüğü (bit) - OpenBAN için varsayılan: 16-bit (8, 12 veya 16-bit olabilir)")]
    public int adcResolution = 16;
    
    [Tooltip("Operasyon voltajı (V) - EDA sensörü için: 3V (VCC)")]
    public float operatingVoltage = 3.0f;
    
    [Tooltip("Transfer fonksiyonu sabiti - EDA sensörü datasheet'ine göre: 0.12")]
    public float transferConstant = 0.12f;
    
    [Tooltip("EDA ölçüm aralığı (µS) - EDA sensörü için: 0-25µS @ VCC=3V")]
    public Vector2 edaRange = new Vector2(0f, 25f);
    
    [Header("Veri Validasyonu")]
    [Tooltip("Minimum EDA baseline değeri (µS) - Training verisi 1.89µS'den başlıyor. 0 değeri sensör bağlantı sorununu gösterir.")]
    public float minimumEDABaseline = 0.5f;
    
    [Tooltip("EDA değeri baseline'ın altına düştüğünde warning logla")]
    public bool warnOnLowEDA = true;
    
    [Header("Plux Cihaz Ayarları")]
    [Tooltip("Plux cihazının MAC adresi (örn: 00:07:80:0F:30:17). Boş bırakılırsa otomatik bulunur")]
    public string pluxMacAddress = "00:07:80:0F:30:17";
    
    [Tooltip("Otomatik olarak cihazı tarayıp bağlan")]
    public bool autoConnect = true;
    
    [Tooltip("Otomatik tarama aralığı (saniye)")]
    public float scanInterval = 5f;
    
    [Tooltip("Tarama için kullanılacak domain'ler (Hybrid8Test.cs'deki gibi varsayılan: BTH)")]
    public List<string> scanDomains = new List<string> { "BTH" };
    
    [Header("Performans Ayarları")]
    [Tooltip("Özellik hesaplama aralığı (saniye) - 1000 Hz veri gelirken özellikleri bu aralıkta hesapla")]
    public float featureCalculationInterval = 0.5f;
    
    [Header("Debug")]
    public bool showDebugLogs = true;
    
    // ========== VERİ TOPLAMA ==========
    private List<GSRSample> samples = new List<GSRSample>();
    private float lastGSRValue = 0f;
    
    // ========== HESAPLANAN ÖZELLİKLER ==========
    private GSRFeatures currentFeatures = new GSRFeatures();
    public event Action<GSRFeatures> OnFeaturesCalculated;
    public event Action OnDataCollectionStarted;
    public event Action<float> OnGSRValueUpdated; // Yeni GSR değeri geldiğinde tetiklenir
    
    // ========== PERFORMANS OPTİMİZASYONU ==========
    private float lastFeatureCalculationTime = 0f;
    private bool needsFeatureCalculation = false;
    
    // ========== PLUX CİHAZ YÖNETİMİ ==========
    private PluxDeviceManager pluxManager;
    private bool isPluxConnected = false;
    private bool isAcquisitionStarted = false;
    private List<string> foundDevices = new List<string>();
    private bool isScanning = false;
    private bool isConnecting = false;
    private Coroutine autoScanCoroutine;
    
    // ========== GC OPTİMİZASYONU: YENİDEN KULLANILABİLİR BUFFER'LAR ==========
    private List<float> gsrValuesBuffer = new List<float>();
    private float cachedCutoffTime = 0f;
    private int featuresCalculatedCount = 0;

    // ========== RECONNECT BACKOFF ==========
    private float currentBackoffInterval;
    private const float MAX_BACKOFF_INTERVAL = 60f;
    private const float BACKOFF_MULTIPLIER = 2f;
    private float lastDataReceivedTime = 0f;
    private const float DATA_TIMEOUT_SECONDS = 10f;
    private bool isRecoveringFromError = false;

    // ========== FİLTRELEME SİSTEMİ ==========
    private Queue<float> filterBuffer = new Queue<float>();
    private float filterSum = 0f; // Performans için toplamı cache'ler
    
    // ========== SABİTLER ==========
    private const int MIN_SAMPLES_FOR_CALCULATION = 2;
    private const int MIN_SAMPLES_FOR_PEAK_VALLEY = 3;
    private const int TARGET_ANALYSIS_RATE_HZ = 10;
    private const int DEBUG_SAMPLE_COUNT = 10;
    private const int DEBUG_FEATURE_CALCULATION_COUNT = 5;
    private const float COROUTINE_WAIT_SECONDS = 1f;
    private const int SINGLE_CHANNEL_INDEX = 0;
    private const string DEFAULT_SCAN_DOMAIN = "BTH";
    private const int MAC_PREFIX_LENGTH = 3;
    private const int MIN_FILTER_WINDOW_SIZE = 1;
    private const int MAX_FILTER_WINDOW_SIZE = 100;
    private const int MIN_SAMPLES_FOR_WAVE_DETECTION = 50; // Dalga tespiti için minimum örnek sayısı
    private const float RISE_DECAY_RATIO_THRESHOLD = 0.3f; // Yükselme/sönümlenme oranı eşiği
    
    void Start()
    {
        if (autoConnect)
        {
            currentBackoffInterval = scanInterval;
            InitializePluxDevice();
            // Otomatik tarama coroutine'ini başlat
            StartAutoScanning();
        }
    }
    
    void Update()
    {
        if (needsFeatureCalculation && startCollecting)
        {
            TryCalculateFeatures();
        }

        // Veri timeout kontrolü: belirli süre veri gelmezse bağlantı kopmuş olabilir
        if (isAcquisitionStarted && startCollecting && lastDataReceivedTime > 0f)
        {
            if (Time.time - lastDataReceivedTime > DATA_TIMEOUT_SECONDS)
            {
                Debug.LogWarning($"[GSRDataCollector] {DATA_TIMEOUT_SECONDS}s boyunca veri alınamadı! Bağlantı kontrol ediliyor...");
                lastDataReceivedTime = Time.time; // Tekrar tekrar uyarı vermemek için sıfırla
            }
        }
    }
    
    /// <summary>
    /// Özellik hesaplama zamanı geldiyse hesaplar
    /// </summary>
    private void TryCalculateFeatures()
    {
        float currentTime = Time.time;
        if (currentTime - lastFeatureCalculationTime >= featureCalculationInterval)
        {
            CalculateFeatures();
            lastFeatureCalculationTime = currentTime;
            needsFeatureCalculation = false;
        }
    }
    
    private volatile bool isShuttingDown = false;

    void OnDestroy()
    {
        isShuttingDown = true;
        StopAutoScanning();

        // Plux native çağrıları main thread'i bloke edebilir (özellikle cihaz bağlantısı kopukken).
        // Bu nedenle cleanup'ı background thread'de timeout ile çalıştırıyoruz.
        var cleanupThread = new Thread(() =>
        {
            try { StopCollecting(); }
            catch (Exception e) { Debug.LogWarning($"[GSRDataCollector] StopCollecting hatası (OnDestroy): {e.Message}"); }

            try { DisconnectPluxDevice(); }
            catch (Exception e) { Debug.LogWarning($"[GSRDataCollector] DisconnectPluxDevice hatası (OnDestroy): {e.Message}"); }
        });
        cleanupThread.IsBackground = true;
        cleanupThread.Start();

        // Maksimum 2 saniye bekle, sonra devam et (Unity'nin donmasını önle)
        if (!cleanupThread.Join(2000))
        {
            Debug.LogWarning("[GSRDataCollector] Plux cleanup 2s içinde tamamlanamadı, atlanıyor.");
        }
    }
    
    void OnDisable()
    {
        isShuttingDown = true;
        StopAutoScanning();
    }
    
    /// <summary>
    /// PluxDeviceManager'ı başlat ve callback'leri ayarla
    /// </summary>
    private void InitializePluxDevice()
    {
        try
        {
            pluxManager = new PluxDeviceManager(
                OnScanResults,
                OnConnectionDone,
                OnAcquisitionStarted,
                OnGSRDataReceived,
                OnEventDetected,
                OnExceptionRaised
            );
            
            // Debug için WelcomeFunction çağrısı (Hybrid8Test.cs'den)
            pluxManager.WelcomeFunctionUnity();
            
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] PluxDeviceManager başlatıldı");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GSRDataCollector] PluxDeviceManager başlatma hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// Otomatik tarama coroutine'ini başlat (5 saniyede bir)
    /// </summary>
    private void StartAutoScanning()
    {
        if (autoScanCoroutine != null)
        {
            StopCoroutine(autoScanCoroutine);
        }
        
        autoScanCoroutine = StartCoroutine(AutoScanCoroutine());
        
        if (showDebugLogs)
            Debug.Log($"[GSRDataCollector] Otomatik tarama başlatıldı (her {scanInterval} saniyede bir)");
    }
    
    /// <summary>
    /// Otomatik taramayı durdur
    /// </summary>
    private void StopAutoScanning()
    {
        if (autoScanCoroutine != null)
        {
            StopCoroutine(autoScanCoroutine);
            autoScanCoroutine = null;
        }
    }
    
    /// <summary>
    /// Otomatik tarama coroutine'i - belirli aralıklarla cihazları tarar
    /// </summary>
    private System.Collections.IEnumerator AutoScanCoroutine()
    {
        // Reconnect sırasında backoff'u kademeli artır
        while (autoConnect && !isPluxConnected)
        {
            if (isPluxConnected)
            {
                if (showDebugLogs)
                    Debug.Log("[GSRDataCollector] Cihaz bağlı, otomatik tarama durduruldu");
                currentBackoffInterval = scanInterval; // Backoff'u sıfırla
                yield break;
            }

            if (isScanning || isConnecting)
            {
                yield return new WaitForSeconds(COROUTINE_WAIT_SECONDS);
                continue;
            }

            if (pluxManager != null)
            {
                ScanForDevices();
            }
            else
            {
                InitializePluxDevice();
            }

            if (showDebugLogs)
                Debug.Log($"[GSRDataCollector] Sonraki tarama {currentBackoffInterval:F1}s sonra...");

            yield return new WaitForSeconds(currentBackoffInterval);

            // Exponential backoff: her başarısız denemede aralığı artır
            currentBackoffInterval = Mathf.Min(currentBackoffInterval * BACKOFF_MULTIPLIER, MAX_BACKOFF_INTERVAL);
        }
    }
    
    /// <summary>
    /// Plux cihazlarını tara
    /// </summary>
    public void ScanForDevices()
    {
        if (pluxManager == null)
        {
            Debug.LogError("[GSRDataCollector] PluxDeviceManager başlatılmamış!");
            return;
        }
        
        if (isScanning)
        {
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Tarama zaten devam ediyor...");
            return;
        }
        
        isScanning = true;
        foundDevices.Clear();
        EnsureScanDomains();
        
        pluxManager.GetDetectableDevicesUnity(scanDomains);
        
        if (showDebugLogs)
            Debug.Log($"[GSRDataCollector] Cihazlar taranıyor... (Domain: {string.Join(", ", scanDomains)})");
    }
    
    /// <summary>
    /// Scan domain'lerinin ayarlandığından emin olur
    /// </summary>
    private void EnsureScanDomains()
    {
        if (scanDomains == null || scanDomains.Count == 0)
        {
            scanDomains = new List<string> { DEFAULT_SCAN_DOMAIN };
        }
    }
    
    /// <summary>
    /// Belirtilen MAC adresine bağlan
    /// </summary>
    public void ConnectToDevice(string macAddress)
    {
        if (!CanConnect())
            return;
        
        isConnecting = true;
        string cleanMacAddress = CleanMacAddress(macAddress);
        pluxMacAddress = cleanMacAddress;
        pluxManager.PluxDev(cleanMacAddress);
        
        if (showDebugLogs)
            Debug.Log($"[GSRDataCollector] Bağlanılıyor: {cleanMacAddress} (Orijinal: {macAddress})");
    }
    
    /// <summary>
    /// Bağlantı yapılabilir mi kontrol eder
    /// </summary>
    private bool CanConnect()
    {
        if (pluxManager == null)
        {
            Debug.LogError("[GSRDataCollector] PluxDeviceManager başlatılmamış!");
            return false;
        }
        
        if (isConnecting)
        {
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Bağlantı zaten devam ediyor...");
            return false;
        }
        
        if (isPluxConnected)
        {
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Zaten bağlı!");
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// MAC adresinden domain prefix'ini (BTH/BLE) kaldırır
    /// </summary>
    private string CleanMacAddress(string macAddress)
    {
        if (macAddress.StartsWith("BTH") || macAddress.StartsWith("BLE"))
        {
            return macAddress.Substring(MAC_PREFIX_LENGTH);
        }
        return macAddress;
    }
    
    /// <summary>
    /// Veri toplamayı başlat
    /// </summary>
    public void StartCollecting()
    {
        if (!CanStartCollecting())
            return;
        
        try
        {
            List<int> activeChannels = new List<int> { gsrChannel };
            pluxManager.StartAcquisitionUnity(samplingRate, activeChannels, adcResolution);
            
            startCollecting = true;
            samples.Clear();
            ResetFilter();
            
            if (showDebugLogs)
                Debug.Log($"[GSRDataCollector] Veri toplama başlatıldı (Kanal: {gsrChannel}, Rate: {samplingRate}Hz, Resolution: {adcResolution}bit, Filtreleme: {(enableFiltering ? "Aktif" : "Pasif")})");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GSRDataCollector] Veri toplama başlatma hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// Veri toplama başlatılabilir mi kontrol eder
    /// </summary>
    private bool CanStartCollecting()
    {
        if (!isPluxConnected)
        {
            Debug.LogWarning("[GSRDataCollector] Plux cihazı bağlı değil!");
            return false;
        }
        
        if (isAcquisitionStarted)
        {
            if (showDebugLogs)
                Debug.LogWarning("[GSRDataCollector] Veri toplama zaten başlatılmış!");
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Veri toplamayı durdur
    /// </summary>
    public void StopCollecting()
    {
        if (pluxManager != null && isAcquisitionStarted)
        {
            pluxManager.StopAcquisitionUnity();
            isAcquisitionStarted = false;
            startCollecting = false;
            
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Veri toplama durduruldu");
        }
    }
    
    /// <summary>
    /// Plux cihazından bağlantıyı kes
    /// </summary>
    public void DisconnectPluxDevice()
    {
        StopCollecting();
        
        if (pluxManager != null && isPluxConnected)
        {
            pluxManager.DisconnectPluxDev();
            isPluxConnected = false;
            
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Plux cihazından bağlantı kesildi");
        }
    }
    
    /// <summary>
    /// PluxDeviceManager'dan gelen ham veriyi işle
    /// </summary>
    private void OnGSRDataReceived(int nSeq, int[] dataIn)
    {
        if (!ShouldProcessData(dataIn))
            return;

        lastDataReceivedTime = Time.time;

        int rawValue = dataIn[SINGLE_CHANNEL_INDEX];
        float gsrValue = ConvertToGSRValue(rawValue);
        
        // Filtreleme uygula (eğer aktifse)
        float filteredGSRValue = enableFiltering ? ApplyMovingAverageFilter(gsrValue) : gsrValue;
        
        LogDataReceived(nSeq, rawValue, filteredGSRValue);
        CreateAndStoreSample(rawValue, filteredGSRValue);
        CleanupOldSamples();
        needsFeatureCalculation = true;
    }
    
    /// <summary>
    /// Veri işlenmeli mi kontrol eder
    /// </summary>
    private bool ShouldProcessData(int[] dataIn)
    {
        return startCollecting && dataIn != null && dataIn.Length > 0;
    }
    
    /// <summary>
    /// Alınan veriyi loglar (ilk birkaç sample için)
    /// </summary>
    private void LogDataReceived(int nSeq, int rawValue, float gsrValue)
    {
        if (showDebugLogs && samples.Count < DEBUG_SAMPLE_COUNT)
        {
            Debug.Log($"[GSRDataCollector] Veri alındı - nSeq: {nSeq}, Raw: {rawValue}, GSR: {gsrValue:F6} µS, Sample count: {samples.Count + 1}");
        }
    }
    
    /// <summary>
    /// Sample oluşturur ve listeye ekler
    /// </summary>
    private void CreateAndStoreSample(int rawValue, float gsrValue)
    {
        GSRSample sample = new GSRSample
        {
            timestamp = Time.time,
            gsrValue = gsrValue,
            rawValue = rawValue
        };
        
        samples.Add(sample);
        lastGSRValue = gsrValue;
        OnGSRValueUpdated?.Invoke(gsrValue);
    }
    
    /// <summary>
    /// Zaman penceresi dışında kalan eski sample'ları temizler
    /// </summary>
    private void CleanupOldSamples()
    {
        cachedCutoffTime = Time.time - timeWindow;
        RemoveOldSamples();
    }
    
    /// <summary>
    /// Ham ADC değerini EDA (Electrodermal Activity) değerine çevir
    /// Transfer fonksiyonu: EDA(µS) = (ADC / 2^n) × VCC / 0.12
    /// NOT: Training verisi 1.89-28.53 µS aralığında. 0 değeri drift sorununa neden olur.
    /// </summary>
    private float ConvertToGSRValue(int rawValue)
    {
        int twoToN = 1 << adcResolution;
        float normalizedADC = (float)rawValue / twoToN;
        float edaMicroSiemens = (normalizedADC * operatingVoltage) / transferConstant;
        
        // Minimum baseline kontrolü - 0 değeri sensör bağlantı sorununu gösterir
        // Training verisi minimum 1.89 µS kullandığı için 0 değeri drift'e neden olur
        if (edaMicroSiemens < minimumEDABaseline)
        {
            if (warnOnLowEDA && showDebugLogs && samples.Count < DEBUG_SAMPLE_COUNT)
            {
                Debug.LogWarning($"[GSRDataCollector] EDA değeri çok düşük: {edaMicroSiemens:F4} µS (baseline: {minimumEDABaseline} µS). " +
                    "Sensör bağlantısını kontrol edin. Training verisi 1.89-28.53 µS aralığında.");
            }
            // Minimum baseline'ı uygula (drift'i önlemek için)
            edaMicroSiemens = Mathf.Max(edaMicroSiemens, minimumEDABaseline);
        }
        
        return Mathf.Clamp(edaMicroSiemens, edaRange.x, edaRange.y);
    }
    
    /// <summary>
    /// GSR özelliklerini hesapla
    /// </summary>
    private void CalculateFeatures()
    {
        if (samples.Count < MIN_SAMPLES_FOR_CALCULATION)
            return;
        
        ExtractGSRValues();
        currentFeatures.Reset();
        
        CalculateBasicStatistics();
        CalculatePeaksAndValleys(gsrValuesBuffer);
        NormalizePeakCounts();
        CalculateRatio();
        
        LogFeatureCalculation();
        OnFeaturesCalculated?.Invoke(currentFeatures);
    }
    
    /// <summary>
    /// Sample'lardan GSR değerlerini buffer'a çıkarır
    /// </summary>
    private void ExtractGSRValues()
    {
        gsrValuesBuffer.Clear();
        for (int i = 0; i < samples.Count; i++)
        {
            gsrValuesBuffer.Add(samples[i].gsrValue);
        }
    }
    
    /// <summary>
    /// Temel istatistikleri hesaplar (Mean, SD, Variance, Min, Max)
    /// </summary>
    private void CalculateBasicStatistics()
    {
        currentFeatures.Mean = CalculateMean(gsrValuesBuffer);
        currentFeatures.SD = CalculateStandardDeviation(gsrValuesBuffer);
        currentFeatures.Variance = currentFeatures.SD * currentFeatures.SD;
        currentFeatures.Minimum = CalculateMin(gsrValuesBuffer);
        currentFeatures.Maximum = CalculateMax(gsrValuesBuffer);
    }
    
    /// <summary>
    /// Peak ve Valley sayılarını normalize eder
    /// VREED dataset'ine göre: peak sayısı / toplam sample sayısı
    /// Training verisinde değerler ~0 ile ~8.15E-05 aralığında
    /// 
    /// ÖNEMLİ: Training verisi muhtemelen ~100 Hz sampling rate kullandı.
    /// Plux 1000 Hz'de çalıştığı için, aynı zaman diliminde 10 kat daha fazla sample var.
    /// Bu nedenle training sampling rate'ine göre normalize etmek gerekiyor.
    /// </summary>
    private void NormalizePeakCounts()
    {
        if (!normalizePeakCount)
            return;
        
        if (normalizeBysamples && samples.Count > 0)
        {
            // Training verisiyle uyumlu normalizasyon
            // Training'de 100 Hz sampling rate varsayılıyor
            // Plux 1000 Hz'de çalışırken aynı zaman diliminde 10x sample var
            // Bu yüzden training sampling rate'ine göre eşdeğer sample sayısı hesaplanır
            
            // Gerçek sampling rate'i hesapla (samples / timeWindow)
            float actualTimeSpan = samples.Count > 1 ? 
                (samples[samples.Count - 1].timestamp - samples[0].timestamp) : timeWindow;
            float actualSamplingRate = actualTimeSpan > 0 ? samples.Count / actualTimeSpan : samplingRate;
            
            // Training eşdeğeri sample sayısı
            float trainingSamplesEquivalent = samples.Count * (trainingSamplingRate / actualSamplingRate);
            
            if (trainingSamplesEquivalent > 0)
            {
                currentFeatures.Number_of_Peaks /= trainingSamplesEquivalent;
                currentFeatures.Number_of_Valleys /= trainingSamplesEquivalent;
            }
        }
        else if (timeWindow > 0f)
        {
            // Zaman penceresi bazlı normalizasyon (peak/saniye)
            currentFeatures.Number_of_Peaks /= timeWindow;
            currentFeatures.Number_of_Valleys /= timeWindow;
        }
    }
    
    /// <summary>
    /// Peak/Valley oranını hesaplar
    /// </summary>
    private void CalculateRatio()
    {
        if (currentFeatures.Number_of_Valleys > 0)
        {
            currentFeatures.Ratio = currentFeatures.Number_of_Peaks / currentFeatures.Number_of_Valleys;
        }
        else if (currentFeatures.Number_of_Peaks > 0)
        {
            currentFeatures.Ratio = float.MaxValue;
        }
        else
        {
            currentFeatures.Ratio = 0f;
        }
    }
    
    /// <summary>
    /// Özellik hesaplama sonuçlarını loglar
    /// </summary>
    private void LogFeatureCalculation()
    {
        if (showDebugLogs && featuresCalculatedCount < DEBUG_FEATURE_CALCULATION_COUNT)
        {
            featuresCalculatedCount++;
            float timeWindowActual = samples.Count > 0 ? 
                (samples[samples.Count - 1].timestamp - samples[0].timestamp) : 0f;
            
            // Raw peak sayısını geri hesapla
            float rawPeaks = 0f;
            if (normalizePeakCount && normalizeBysamples && samples.Count > 0)
            {
                rawPeaks = currentFeatures.Number_of_Peaks * samples.Count;
            }
            else if (normalizePeakCount && timeWindow > 0f)
            {
                rawPeaks = currentFeatures.Number_of_Peaks * timeWindow;
            }
            else
            {
                rawPeaks = currentFeatures.Number_of_Peaks;
            }
            
            Debug.Log($"[GSRDataCollector] Özellikler hesaplandı #{featuresCalculatedCount} - " +
                      $"Samples: {samples.Count}, TimeWindow: {timeWindowActual:F2}s, " +
                      $"Mean: {currentFeatures.Mean:F6}, SD: {currentFeatures.SD:F6}, " +
                      $"Peaks (raw): {rawPeaks:F0}, Peaks (normalized): {currentFeatures.Number_of_Peaks:E2}");
        }
    }
    
    /// <summary>
    /// Peak ve Valley sayılarını hesapla
    /// Yeni sistem: Yükselme ve sönümlenme dalgalarını tespit eder (stres tepkilerini yakalar)
    /// </summary>
    private void CalculatePeaksAndValleys(List<float> values)
    {
        if (values.Count < MIN_SAMPLES_FOR_WAVE_DETECTION)
        {
            currentFeatures.Number_of_Peaks = 0f;
            currentFeatures.Number_of_Valleys = 0f;
            return;
        }
        
        // Dalga yapısını tespit et (yükselme + tepe + sönümlenme)
        int waves = DetectStressWaves(values);
        currentFeatures.Number_of_Peaks = waves;
        
        // Valley sayısı genellikle peak sayısına yakındır (dalga yapısı nedeniyle)
        // Ancak daha hassas bir tespit için valley'leri de sayabiliriz
        currentFeatures.Number_of_Valleys = DetectValleys(values);
    }
    
    /// <summary>
    /// Stres dalgalarını tespit eder (yükselme + tepe + sönümlenme)
    /// Gerçek stres tepkilerini yakalamak için geniş bir pencere kullanır
    /// </summary>
    private int DetectStressWaves(List<float> values)
    {
        int waveCount = 0;
        int i = 0;
        int minRequiredSamples = minRiseSamples + minDecaySamples + 5; // Tepe noktası için ekstra alan
        
        while (i < values.Count - minRequiredSamples)
        {
            // Yükselme fazını tespit et
            int riseStart = i;
            int riseEnd = DetectRisePhase(values, riseStart);
            
            if (riseEnd == -1)
            {
                i++;
                continue;
            }
            
            // Tepe noktasını bul
            int peakIndex = FindPeakInRange(values, riseEnd, Mathf.Min(riseEnd + 10, values.Count - 1));
            
            if (peakIndex == -1)
            {
                i = riseEnd + 1;
                continue;
            }
            
            // Sönümlenme fazını tespit et
            int decayStart = peakIndex;
            int decayEnd = DetectDecayPhase(values, decayStart);
            
            if (decayEnd == -1)
            {
                i = peakIndex + 1;
                continue;
            }
            
            // Dalga genliğini kontrol et
            float waveAmplitude = values[peakIndex] - values[riseStart];
            if (waveAmplitude >= waveAmplitudeThreshold)
            {
                waveCount++;
                // Bir sonraki dalgayı aramak için sönümlenme sonuna geç
                i = decayEnd;
            }
            else
            {
                // Eşik altı, bir sonraki noktadan devam et
                i = peakIndex + 1;
            }
        }
        
        return waveCount;
    }
    
    /// <summary>
    /// Yükselme fazını tespit eder (artış trendi)
    /// </summary>
    private int DetectRisePhase(List<float> values, int startIndex)
    {
        if (startIndex + minRiseSamples >= values.Count)
            return -1;
        
        int consecutiveRises = 0;
        int maxConsecutiveRises = 0;
        int riseEnd = -1;
        
        for (int i = startIndex + 1; i < startIndex + minRiseSamples * 2 && i < values.Count; i++)
        {
            if (values[i] > values[i - 1])
            {
                consecutiveRises++;
                if (consecutiveRises > maxConsecutiveRises)
                {
                    maxConsecutiveRises = consecutiveRises;
                    riseEnd = i;
                }
            }
            else
            {
                consecutiveRises = 0;
            }
        }
        
        // Yeterli yükselme tespit edildi mi?
        if (maxConsecutiveRises >= minRiseSamples / 2) // En az yarısı kadar artış olmalı
        {
            return riseEnd;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Sönümlenme fazını tespit eder (azalış trendi)
    /// </summary>
    private int DetectDecayPhase(List<float> values, int startIndex)
    {
        if (startIndex + minDecaySamples >= values.Count)
            return -1;
        
        int consecutiveDecays = 0;
        int maxConsecutiveDecays = 0;
        int decayEnd = -1;
        
        for (int i = startIndex + 1; i < startIndex + minDecaySamples * 2 && i < values.Count; i++)
        {
            if (values[i] < values[i - 1])
            {
                consecutiveDecays++;
                if (consecutiveDecays > maxConsecutiveDecays)
                {
                    maxConsecutiveDecays = consecutiveDecays;
                    decayEnd = i;
                }
            }
            else
            {
                consecutiveDecays = 0;
            }
        }
        
        // Yeterli sönümlenme tespit edildi mi?
        if (maxConsecutiveDecays >= minDecaySamples / 2) // En az yarısı kadar azalış olmalı
        {
            return decayEnd;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Belirli bir aralıkta tepe noktasını bulur
    /// </summary>
    private int FindPeakInRange(List<float> values, int startIndex, int endIndex)
    {
        if (startIndex >= endIndex || endIndex >= values.Count)
            return -1;
        
        float maxValue = values[startIndex];
        int peakIndex = startIndex;
        
        for (int i = startIndex + 1; i <= endIndex; i++)
        {
            if (values[i] > maxValue)
            {
                maxValue = values[i];
                peakIndex = i;
            }
        }
        
        return peakIndex;
    }
    
    /// <summary>
    /// Valley (dip noktaları) sayısını tespit eder
    /// </summary>
    private int DetectValleys(List<float> values)
    {
        int valleyCount = 0;
        int i = 0;
        int minRequiredSamples = minRiseSamples + minDecaySamples + 5;
        
        while (i < values.Count - minRequiredSamples)
        {
            // Azalış fazını tespit et
            int fallStart = i;
            int fallEnd = DetectFallPhase(values, fallStart);
            
            if (fallEnd == -1)
            {
                i++;
                continue;
            }
            
            // Valley noktasını bul
            int valleyIndex = FindValleyInRange(values, fallEnd, Mathf.Min(fallEnd + 10, values.Count - 1));
            
            if (valleyIndex == -1)
            {
                i = fallEnd + 1;
                continue;
            }
            
            // Yükselme fazını tespit et (valley'den sonra)
            int recoveryStart = valleyIndex;
            int recoveryEnd = DetectRisePhase(values, recoveryStart);
            
            if (recoveryEnd == -1)
            {
                i = valleyIndex + 1;
                continue;
            }
            
            // Valley genliğini kontrol et
            float valleyAmplitude = values[fallStart] - values[valleyIndex];
            if (valleyAmplitude >= waveAmplitudeThreshold)
            {
                valleyCount++;
                i = recoveryEnd;
            }
            else
            {
                i = valleyIndex + 1;
            }
        }
        
        return valleyCount;
    }
    
    /// <summary>
    /// Azalış fazını tespit eder (valley tespiti için)
    /// </summary>
    private int DetectFallPhase(List<float> values, int startIndex)
    {
        if (startIndex + minRiseSamples >= values.Count)
            return -1;
        
        int consecutiveFalls = 0;
        int maxConsecutiveFalls = 0;
        int fallEnd = -1;
        
        for (int i = startIndex + 1; i < startIndex + minRiseSamples * 2 && i < values.Count; i++)
        {
            if (values[i] < values[i - 1])
            {
                consecutiveFalls++;
                if (consecutiveFalls > maxConsecutiveFalls)
                {
                    maxConsecutiveFalls = consecutiveFalls;
                    fallEnd = i;
                }
            }
            else
            {
                consecutiveFalls = 0;
            }
        }
        
        if (maxConsecutiveFalls >= minRiseSamples / 2)
        {
            return fallEnd;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Belirli bir aralıkta valley (dip) noktasını bulur
    /// </summary>
    private int FindValleyInRange(List<float> values, int startIndex, int endIndex)
    {
        if (startIndex >= endIndex || endIndex >= values.Count)
            return -1;
        
        float minValue = values[startIndex];
        int valleyIndex = startIndex;
        
        for (int i = startIndex + 1; i <= endIndex; i++)
        {
            if (values[i] < minValue)
            {
                minValue = values[i];
                valleyIndex = i;
            }
        }
        
        return valleyIndex;
    }
    
    // ========== İSTATİSTİKSEL HESAPLAMA METODLARI ==========
    
    private float CalculateMean(List<float> values)
    {
        if (values.Count == 0)
            return 0f;
        
        float sum = 0f;
        for (int i = 0; i < values.Count; i++)
            sum += values[i];
        
        return sum / values.Count;
    }
    
    private float CalculateStandardDeviation(List<float> values)
    {
        if (values.Count == 0)
            return 0f;
        
        float mean = CalculateMean(values);
        float sumSquaredDiff = 0f;
        
        for (int i = 0; i < values.Count; i++)
        {
            float diff = values[i] - mean;
            sumSquaredDiff += diff * diff;
        }
        
        return Mathf.Sqrt(sumSquaredDiff / values.Count);
    }
    
    private float CalculateMin(List<float> values)
    {
        if (values.Count == 0)
            return 0f;
        
        float min = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] < min)
                min = values[i];
        }
        
        return min;
    }
    
    private float CalculateMax(List<float> values)
    {
        if (values.Count == 0)
            return 0f;
        
        float max = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] > max)
                max = values[i];
        }
        
        return max;
    }
    
    /// <summary>
    /// Hesaplanan özellikleri al
    /// </summary>
    public GSRFeatures GetFeatures()
    {
        return currentFeatures;
    }
    
    /// <summary>
    /// Plux cihaz bağlantı durumunu kontrol et
    /// </summary>
    public bool IsConnected()
    {
        return isPluxConnected && pluxManager != null;
    }
    
    /// <summary>
    /// Son alınan GSR değerini döndürür
    /// </summary>
    public float GetLastGSRValue()
    {
        return lastGSRValue;
    }
    
    // ========== PLUX DEVICE MANAGER CALLBACK'LERİ ==========
    
    /// <summary>
    /// Tarama sonuçları geldiğinde çağrılır
    /// </summary>
    private void OnScanResults(List<string> listDevices)
    {
        isScanning = false;
        foundDevices = listDevices;
        
        if (showDebugLogs)
            Debug.Log($"[GSRDataCollector] Tarama tamamlandı: {listDevices.Count} cihaz bulundu");
        
        if (isPluxConnected)
        {
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Zaten bağlı, yeni tarama sonuçları göz ardı edildi");
            return;
        }
        
        if (listDevices.Count > 0)
        {
            string deviceToConnect = FindDeviceToConnect(listDevices);
            if (deviceToConnect != null)
            {
                if (showDebugLogs)
                    Debug.Log($"[GSRDataCollector] Cihaz bulundu! Bağlanılıyor: {deviceToConnect}");
                ConnectToDevice(deviceToConnect);
            }
        }
        else
        {
            if (showDebugLogs)
                Debug.Log($"[GSRDataCollector] Cihaz bulunamadı, {scanInterval} saniye sonra tekrar taranacak...");
        }
    }
    
    /// <summary>
    /// Bağlanılacak cihazı bulur
    /// </summary>
    private string FindDeviceToConnect(List<string> listDevices)
    {
        if (string.IsNullOrEmpty(pluxMacAddress))
        {
            return listDevices[0];
        }
        
        string deviceToConnect = FindDeviceByMacAddress(listDevices);
        if (deviceToConnect == null && showDebugLogs)
        {
            Debug.LogWarning($"[GSRDataCollector] Belirtilen MAC adresi bulunamadı: {pluxMacAddress}. Bulunan cihazlar: {string.Join(", ", listDevices)}. Tekrar taranacak...");
        }
        
        return deviceToConnect;
    }
    
    /// <summary>
    /// MAC adresine göre cihazı bulur
    /// </summary>
    private string FindDeviceByMacAddress(List<string> listDevices)
    {
        string cleanMac = pluxMacAddress.Trim();
        string bthMac = "BTH" + cleanMac;
        string bleMac = "BLE" + cleanMac;
        
        if (listDevices.Contains(cleanMac))
            return cleanMac;
        
        if (listDevices.Contains(bthMac))
            return bthMac;
        
        if (listDevices.Contains(bleMac))
            return bleMac;
        
        return null;
    }
    
    /// <summary>
    /// ConnectionDone callback - Hybrid8Test.cs'deki ConnectionDone gibi
    /// Bağlantı başarılıysa otomatik olarak StartButtonFunction işlevine geçer
    /// </summary>
    private void OnConnectionDone(bool connectionStatus)
    {
        isConnecting = false;
        isPluxConnected = connectionStatus;
        
        if (connectionStatus)
        {
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Plux cihazına başarıyla bağlandı");
            
            // Bağlantı başarılıysa, otomatik taramayı durdur
            StopAutoScanning();

            // Backoff'u sıfırla
            currentBackoffInterval = scanInterval;

            // Hybrid8Test.cs mantığı: Bağlantı başarılıysa StartButtonFunction işlevine geç
            // StartButtonFunction işlevi: Acquisition başlat
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Bağlantı başarılı! Veri toplama başlatılıyor...");
            StartCollecting();
        }
        else
        {
            if (showDebugLogs)
                Debug.LogWarning("[GSRDataCollector] Plux cihazına bağlanılamadı! Tekrar taranacak...");
            
            // Bağlantı başarısız, isConnecting flag'i false yapıldı
            // AutoScanCoroutine zaten çalışıyorsa, 5 saniye sonra tekrar tarayacak
            // MAC adresini temizle ki tekrar deneyebilsin
            pluxMacAddress = "";
        }
    }
    
    /// <summary>
    /// AcquisitionStarted callback - Hybrid8Test.cs'deki AcquisitionStarted gibi
    /// Acquisition başarılıysa veri akışı başlamış olur
    /// </summary>
    private void OnAcquisitionStarted(bool acquisitionStatus, bool exceptionRaised = false, string exceptionDescription = "")
    {
        isAcquisitionStarted = acquisitionStatus;
        
        if (acquisitionStatus)
        {
            if (showDebugLogs)
                Debug.Log("[GSRDataCollector] Acquisition başarıyla başlatıldı - Veri akışı başladı!");
            
            // Hybrid8Test.cs mantığı: Acquisition başarılıysa artık veri akışı başlamış olur
            // OnGSRDataReceived callback'i artık veri alacak
            
            // Veri toplama başladı event'ini tetikle
            OnDataCollectionStarted?.Invoke();
        }
        else
        {
            Debug.LogError($"[GSRDataCollector] Veri toplama başlatılamadı: {exceptionDescription}");
            
            // Acquisition başarısız, tekrar denemek için bağlantıyı kes ve taramayı yeniden başlat
            if (autoConnect)
            {
                DisconnectPluxDevice();
                StartAutoScanning();
            }
        }
    }
    
    private void OnEventDetected(PluxDeviceManager.PluxEvent pluxEvent)
    {
        if (pluxEvent.type == PluxDeviceManager.PluxEvent.PluxEvents.Disconnect)
        {
            isPluxConnected = false;
            isAcquisitionStarted = false;
            startCollecting = false;
            isConnecting = false;
            
            if (showDebugLogs)
                Debug.LogWarning("[GSRDataCollector] Plux cihazından bağlantı kesildi (event). Tekrar taranacak...");
            
            // Bağlantı kesildi, otomatik taramayı yeniden başlat
            if (autoConnect && !isPluxConnected)
            {
                StartAutoScanning();
            }
        }
    }
    
    private void OnExceptionRaised(int exceptionCode, string exceptionDescription)
    {
        Debug.LogError($"[GSRDataCollector] Plux API Hatası [{exceptionCode}]: {exceptionDescription}");

        // Shutdown sırasında kurtarma yapma (StartCoroutine çöker)
        if (isShuttingDown)
            return;

        // Bağlantı kaybı gibi kritik hatalarda otomatik kurtarma başlat
        if (!isRecoveringFromError && autoConnect && this != null && isActiveAndEnabled)
        {
            isRecoveringFromError = true;

            // Mevcut bağlantıyı temizle
            isPluxConnected = false;
            isAcquisitionStarted = false;
            startCollecting = false;
            isConnecting = false;

            Debug.LogWarning($"[GSRDataCollector] Hata kurtarma başlatılıyor (backoff: {currentBackoffInterval:F1}s)...");

            // Exponential backoff ile yeniden tarama başlat
            StartAutoScanning();
            isRecoveringFromError = false;
        }
    }
    
    /// <summary>
    /// Zaman penceresi dışında kalan eski sample'ları temizler
    /// </summary>
    private void RemoveOldSamples()
    {
        int writeIndex = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].timestamp >= cachedCutoffTime)
            {
                samples[writeIndex++] = samples[i];
            }
        }
        
        if (writeIndex < samples.Count)
        {
            samples.RemoveRange(writeIndex, samples.Count - writeIndex);
        }
    }
    
    // ========== FİLTRELEME METODLARI ==========
    
    /// <summary>
    /// Hareketli ortalama filtresi uygular - Parazitleri temizler
    /// </summary>
    private float ApplyMovingAverageFilter(float newValue)
    {
        // Pencere boyutunu geçerli aralıkta tut
        int windowSize = Mathf.Clamp(filterWindowSize, MIN_FILTER_WINDOW_SIZE, MAX_FILTER_WINDOW_SIZE);
        
        // Yeni değeri buffer'a ekle
        filterBuffer.Enqueue(newValue);
        filterSum += newValue;
        
        // Pencere boyutunu aşan eski değerleri çıkar
        while (filterBuffer.Count > windowSize)
        {
            float oldValue = filterBuffer.Dequeue();
            filterSum -= oldValue;
        }
        
        // Ortalama hesapla
        if (filterBuffer.Count == 0)
            return newValue;
        
        return filterSum / filterBuffer.Count;
    }
    
    /// <summary>
    /// Filtre buffer'ını sıfırla
    /// </summary>
    private void ResetFilter()
    {
        filterBuffer.Clear();
        filterSum = 0f;
    }
}

// Veri yapıları
// GC optimizasyonu: class yerine struct kullanarak heap allocation önlenir
[Serializable]
public struct GSRSample
{
    public float timestamp;
    public float gsrValue;
    public int rawValue;
}

/// <summary>
/// GSR özellikleri - CSV dosyasındaki özelliklerle eşleşir
/// </summary>
[Serializable]
public class GSRFeatures
{
    public float Mean = 0f;
    public float SD = 0f;
    public float Variance = 0f;
    public float Minimum = 0f;
    public float Maximum = 0f;
    public float Number_of_Peaks = 0f;
    public float Number_of_Valleys = 0f;
    public float Ratio = 0f;
    
    /// <summary>
    /// Tüm özellikleri sıfırla (GC optimizasyonu: yeni obje oluşturmak yerine)
    /// </summary>
    public void Reset()
    {
        Mean = 0f;
        SD = 0f;
        Variance = 0f;
        Minimum = 0f;
        Maximum = 0f;
        Number_of_Peaks = 0f;
        Number_of_Valleys = 0f;
        Ratio = 0f;
    }
    
    /// <summary>
    /// Dictionary formatına çevir (StressPredictor için)
    /// v3 model: MI bazlı özellik seçimi sonrası sadece 2 GSR özelliği kullanılır.
    /// Log dönüşüm sunucu tarafında (websocket_server.py) otomatik uygulanır.
    /// </summary>
    public Dictionary<string, float> ToDictionary()
    {
        return new Dictionary<string, float>
        {
            // v3 modelin beklediği GSR özellikleri (2 özellik)
            { "SD", SD },
            { "Number of Peaks", Number_of_Peaks }
        };
    }

    /// <summary>
    /// Tüm özellikleri Dictionary formatında döndürür (loglama, debug veya gelecek model eğitimi için)
    /// </summary>
    public Dictionary<string, float> ToFullDictionary()
    {
        return new Dictionary<string, float>
        {
            { "Mean", Mean },
            { "SD", SD },
            { "Variance", Variance },
            { "Minimum", Minimum },
            { "Maximum", Maximum },
            { "Number of Peaks", Number_of_Peaks },
            { "Number of Valleys", Number_of_Valleys },
            { "Ratio", Ratio }
        };
    }
}

