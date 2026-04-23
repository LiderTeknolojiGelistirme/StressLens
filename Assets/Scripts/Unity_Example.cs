/*
 * Unity için LightGBM WebSocket Client Örneği
 * 
 * C#'ın yerleşik System.Net.WebSockets.ClientWebSocket kullanır.
 * Harici paket gerektirmez!
 * 
 * Gereksinimler:
 * - Unity 2021.2 veya üzeri (.NET Standard 2.1 desteği için)
 * - .NET Standard 2.1 veya üzeri
 * 
 * Kullanım:
 * 1. Bu scripti bir GameObject'e ekleyin
 * 2. Özellik değerlerini SetFeature() metoduyla ayarlayın
 * 3. PredictStress() metodunu çağırarak tahmin yapın
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class StressPredictor : MonoBehaviour
{
    [Header("Sunucu Ayarları")]
    [Tooltip("Python WebSocket sunucusunun adresi")]
    public string serverUrl = "ws://localhost:8765";
    
    [Header("Eye Tracking Entegrasyonu")]
    [Tooltip("Eye tracking verilerini kullan")]
    public bool useEyeTracking = true;
    [Tooltip("Eye tracking data collector referansı")]
    public EyeTrackingDataCollector eyeTrackingCollector;
    
    [Header("GSR Entegrasyonu")]
    [Tooltip("GSR verilerini kullan")]
    public bool useGSR = true;
    [Tooltip("GSR data collector referansı")]
    public GSRDataCollector gsrCollector;
    
    [Header("Otomatik Tahmin Ayarları")]
    [Tooltip("Otomatik tahmin yapmayı etkinleştir")]
    public bool autoPredictEnabled = true;
    [Tooltip("Tahmin aralığı (milisaniye) - 30 saniyede bir tahmin yapılır")]
    public float predictionInterval = 30000f; // 30000 milisaniye = 30 saniyede 1 kez
    
    [Header("Debug")]
    [Tooltip("Bağlantı durumunu göster")]
    public bool showDebugLogs = true;
    
    // ========== WEBSOCKET BAĞLANTISI ==========
    private ClientWebSocket websocket;
    private CancellationTokenSource cancellationTokenSource;
    private Task receiveTask;
    private bool isConnected = false;
    
    // ========== VERİ TOPLAMA ==========
    private Dictionary<string, float> features = new Dictionary<string, float>();
    private bool isGSRDataCollectionStarted = false;
    private Coroutine autoPredictCoroutine;
    
    // ========== KALİBRASYON ==========
    private bool wasCalibrated = false;  // Kalibrasyon tamamlandı mı (bir kez tetiklenir)
    
    // ========== GC OPTİMİZASYONU: YENİDEN KULLANILABİLİR BUFFER'LAR ==========
    private StringBuilder jsonBuilderBuffer = new StringBuilder(JSON_BUFFER_SIZE);
    private Dictionary<string, float> tempFeaturesBuffer = new Dictionary<string, float>();
    private readonly object _featureLock = new object();

    // ========== EVENT'LER ==========
    public event Action<StressResult> OnPredictionReceived;
    public event Action<string> OnError;
    public event Action<CalibrationInfo> OnCalibrationProgress;  // Kalibrasyon ilerleme
    public event Action<CalibrationInfo> OnCalibrationComplete;  // Kalibrasyon tamamlandı
    
    // ========== SABİTLER ==========
    private const int JSON_BUFFER_SIZE = 4096;
    private const int RECEIVE_BUFFER_SIZE = 4096;
    private const float AUTO_PREDICT_WAIT_SECONDS = 0.1f;
    private const float RECONNECT_DELAY_MS = 500f;
    private const float MILLISECONDS_TO_SECONDS = 1000f;
    
    async void Start()
    {
        await Connect();
        InitializeEyeTracking();
        InitializeGSR();
        
        if (showDebugLogs)
            Debug.Log("[StressPredictor] GSR veri akışı bekleniyor... Otomatik tahmin GSR veri akışı başladığında başlatılacak.");
    }
    
    /// <summary>
    /// Eye tracking collector'ı başlatır
    /// </summary>
    private void InitializeEyeTracking()
    {
        if (!useEyeTracking)
            return;
        
        if (eyeTrackingCollector == null)
            eyeTrackingCollector = FindAnyObjectByType<EyeTrackingDataCollector>();
        
        if (eyeTrackingCollector != null)
        {
            eyeTrackingCollector.OnFeaturesCalculated += OnEyeTrackingFeaturesCalculated;
            eyeTrackingCollector.StartCollecting();
            
            if (showDebugLogs)
                Debug.Log("[StressPredictor] Eye tracking entegrasyonu başlatıldı");
        }
        else
        {
            Debug.LogWarning("[StressPredictor] EyeTrackingDataCollector bulunamadı!");
        }
    }
    
    /// <summary>
    /// GSR collector'ı başlatır
    /// </summary>
    private void InitializeGSR()
    {
        if (!useGSR)
            return;
        
        if (gsrCollector == null)
            gsrCollector = FindAnyObjectByType<GSRDataCollector>();
        
        if (gsrCollector != null)
        {
            gsrCollector.OnFeaturesCalculated += OnGSRFeaturesCalculated;
            gsrCollector.OnDataCollectionStarted += OnGSRDataCollectionStarted;
            
            if (gsrCollector.IsConnected())
            {
                gsrCollector.StartCollecting();
            }
            else
            {
                if (showDebugLogs)
                    Debug.Log("[StressPredictor] GSR collector bulundu, bağlantı bekleniyor...");
            }
            
            if (showDebugLogs)
                Debug.Log("[StressPredictor] GSR entegrasyonu başlatıldı");
        }
        else
        {
            Debug.LogWarning("[StressPredictor] GSRDataCollector bulunamadı!");
        }
    }
    
    /// <summary>
    /// WebSocket sunucusuna bağlan
    /// </summary>
    public async Task Connect()
    {
        try
        {
            if (websocket != null && websocket.State == WebSocketState.Open)
            {
                if (showDebugLogs)
                    Debug.Log("[StressPredictor] Zaten bağlı!");
                return;
            }
            
            // Eski bağlantıyı temizle
            await Disconnect();
            
            // Yeni WebSocket oluştur
            websocket = new ClientWebSocket();
            cancellationTokenSource = new CancellationTokenSource();
            
            // URL'yi URI'ye çevir
            Uri serverUri = new Uri(serverUrl);
            
            if (showDebugLogs)
                Debug.Log($"[StressPredictor] Bağlanılıyor: {serverUrl}");
            
            // Bağlan
            await websocket.ConnectAsync(serverUri, cancellationTokenSource.Token);
            
            isConnected = true;
            
            if (showDebugLogs)
                Debug.Log("[StressPredictor] Python sunucusuna bağlandı!");
            
            // Mesaj alma görevini başlat
            receiveTask = ReceiveMessages(cancellationTokenSource.Token);
        }
        catch (Exception e)
        {
            isConnected = false;
            Debug.LogError($"[StressPredictor] Bağlantı hatası: {e.Message}");
            OnError?.Invoke($"Bağlantı hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// WebSocket bağlantısını kapat
    /// </summary>
    public async Task Disconnect()
    {
        isConnected = false;
        
        if (cancellationTokenSource != null)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
            cancellationTokenSource = null;
        }
        
        if (receiveTask != null)
        {
            try
            {
                await receiveTask;
            }
            catch (Exception e)
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[StressPredictor] Receive task hatası: {e.Message}");
            }
            receiveTask = null;
        }
        
        if (websocket != null)
        {
            try
            {
                if (websocket.State == WebSocketState.Open || 
                    websocket.State == WebSocketState.CloseReceived || 
                    websocket.State == WebSocketState.CloseSent)
                {
                    await websocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, 
                        "Kapatılıyor", 
                        CancellationToken.None
                    );
                }
            }
            catch (Exception e)
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[StressPredictor] Kapatma hatası: {e.Message}");
            }
            finally
            {
                websocket.Dispose();
                websocket = null;
            }
        }
        
        if (showDebugLogs)
            Debug.Log("[StressPredictor] Bağlantı kapatıldı");
    }
    
    /// <summary>
    /// Sunucudan gelen mesajları dinle
    /// </summary>
    private async Task ReceiveMessages(CancellationToken cancellationToken)
    {
        var buffer = new byte[RECEIVE_BUFFER_SIZE];
        
        while (IsWebSocketOpen() && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await websocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), 
                    cancellationToken
                );
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    HandleServerClose();
                    break;
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    OnMessageReceived(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                HandleReceiveError(e);
                break;
            }
        }
    }
    
    /// <summary>
    /// WebSocket açık mı kontrol eder
    /// </summary>
    private bool IsWebSocketOpen()
    {
        return websocket != null && websocket.State == WebSocketState.Open;
    }
    
    /// <summary>
    /// Sunucu bağlantıyı kapattığında çağrılır
    /// </summary>
    private void HandleServerClose()
    {
        if (showDebugLogs)
            Debug.Log("[StressPredictor] Sunucu bağlantıyı kapattı");
        isConnected = false;
    }
    
    /// <summary>
    /// Mesaj alma hatası durumunda çağrılır
    /// </summary>
    private void HandleReceiveError(Exception e)
    {
        if (showDebugLogs)
            Debug.LogError($"[StressPredictor] Mesaj alma hatası: {e.Message}");
        OnError?.Invoke($"Mesaj alma hatası: {e.Message}");
    }
    
    /// <summary>
    /// Sunucudan gelen mesajı işle
    /// </summary>
    private void OnMessageReceived(string message)
    {
        if (showDebugLogs)
            Debug.Log($"[StressPredictor] Sunucudan gelen: {message}");
        
        try
        {
            StressResult result = JsonUtility.FromJson<StressResult>(message);
            
            if (result.basari)
            {
                HandleSuccessfulPrediction(result);
            }
            else
            {
                HandlePredictionError(result.error);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[StressPredictor] JSON parse hatası: {e.Message}");
            OnError?.Invoke($"JSON parse hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// Başarılı tahmin sonucunu işler
    /// </summary>
    private void HandleSuccessfulPrediction(StressResult result)
    {
        // Kalibrasyon durumunu işle
        HandleCalibrationStatus(result);
        
        if (showDebugLogs)
        {
            var cal = result.kalibrasyon;
            if (cal != null && cal.is_calibrated)
            {
                Debug.Log($"[StressPredictor] Tahmin: {result.durum} | Olasılık: {result.stres_olasilik:P1} | Threshold: {cal.threshold:P1}");
            }
            else if (cal != null)
            {
                Debug.Log($"[StressPredictor] ⏳ Kalibrasyon: {cal.calibration_progress}/{cal.calibration_total} | Olasılık: {result.stres_olasilik:P1}");
            }
            else
            {
                Debug.Log($"[StressPredictor] Tahmin: {result.durum} | Olasılık: {result.stres_olasilik:P1}");
            }
        }
        
        OnPredictionReceived?.Invoke(result);
    }
    
    /// <summary>
    /// Kalibrasyon durumunu işler ve gerekli event'leri tetikler
    /// </summary>
    private void HandleCalibrationStatus(StressResult result)
    {
        var cal = result.kalibrasyon;
        if (cal == null) return;
        
        // Kalibrasyon ilerleme event'ini tetikle
        OnCalibrationProgress?.Invoke(cal);
        
        // Kalibrasyon yeni tamamlandıysa
        if (cal.is_calibrated && !wasCalibrated)
        {
            wasCalibrated = true;
            
            if (showDebugLogs)
            {
                Debug.Log("==========================================");
                Debug.Log("✅ KALİBRASYON TAMAMLANDI!");
                Debug.Log($"   Baseline (sakin hal): {cal.baseline:P1}");
                Debug.Log($"   Dinamik Threshold: {cal.threshold:P1}");
                Debug.Log("==========================================");
            }
            
            OnCalibrationComplete?.Invoke(cal);
        }
    }
    
    /// <summary>
    /// Tahmin hatasını işler
    /// </summary>
    private void HandlePredictionError(string error)
    {
        Debug.LogError($"[StressPredictor] Hata: {error}");
        OnError?.Invoke(error);
    }
    
    /// <summary>
    /// Özellik değerini ayarla
    /// </summary>
    public void SetFeature(string featureName, float value)
    {
        features[featureName] = value;
    }
    
    /// <summary>
    /// Birden fazla özelliği toplu olarak ayarla
    /// </summary>
    public void SetFeatures(Dictionary<string, float> newFeatures)
    {
        foreach (var kvp in newFeatures)
        {
            features[kvp.Key] = kvp.Value;
        }
    }
    
    /// <summary>
    /// Tüm özellikleri temizle
    /// </summary>
    public void ClearFeatures()
    {
        features.Clear();
    }
    
    /// <summary>
    /// Tüm özellikler için random değerler üret (test amaçlı)
    /// </summary>
    public void GenerateRandomFeatures()
    {
        features.Clear();
        
        GenerateRandomFixationFeatures();
        GenerateRandomSaccadeFeatures();
        GenerateRandomBlinkFeatures();
        GenerateRandomMicrosaccadeFeatures();
        GenerateRandomGSRFeatures();
        
        if (showDebugLogs)
            Debug.Log($"[StressPredictor] {features.Count} adet random özellik üretildi");
    }
    
    /// <summary>
    /// Fixation özellikleri için random değerler üretir
    /// </summary>
    private void GenerateRandomFixationFeatures()
    {
        features["Num_of_Fixations"] = UnityEngine.Random.Range(10f, 110f);
        features["Mean_Fixation_Duration"] = UnityEngine.Random.Range(100f, 500f);
        features["Skew_Fixation_Duration"] = UnityEngine.Random.Range(-1.5f, 1.5f);
        features["First_Fixation_Duration"] = UnityEngine.Random.Range(50f, 350f);
    }
    
    /// <summary>
    /// Saccade özellikleri için random değerler üretir
    /// </summary>
    private void GenerateRandomSaccadeFeatures()
    {
        features["Mean_Saccade_Duration"] = UnityEngine.Random.Range(20f, 120f);
        features["Skew_Saccade_Duration"] = UnityEngine.Random.Range(-1.5f, 1.5f);
        features["Skew_Saccade_Amplitude"] = UnityEngine.Random.Range(-1.5f, 1.5f);
        features["Mean_Saccade_Direction"] = UnityEngine.Random.Range(0f, 360f);
        features["SD_Saccade_Direction"] = UnityEngine.Random.Range(10f, 60f);
        features["Max_Saccade_Direction"] = UnityEngine.Random.Range(0f, 360f);
    }
    
    /// <summary>
    /// Blink özellikleri için random değerler üretir
    /// </summary>
    private void GenerateRandomBlinkFeatures()
    {
        features["Num_of_Blink"] = UnityEngine.Random.Range(5f, 35f);
        features["Mean_Blink_Duration"] = UnityEngine.Random.Range(50f, 250f);
        features["Skew_Blink_Duration"] = UnityEngine.Random.Range(-1.5f, 1.5f);
    }
    
    /// <summary>
    /// Microsaccade özellikleri için random değerler üretir
    /// </summary>
    private void GenerateRandomMicrosaccadeFeatures()
    {
        features["Num_of_Microsac"] = UnityEngine.Random.Range(10f, 60f);
        features["Mean_Microsac_Peak_Vel"] = UnityEngine.Random.Range(20f, 120f);
        features["SD_Microsac_Peak_Vel"] = UnityEngine.Random.Range(5f, 35f);
        features["Skew_Microsac_Peak_Vel"] = UnityEngine.Random.Range(-1.5f, 1.5f);
        features["SD_Microsac_Ampl"] = UnityEngine.Random.Range(0.5f, 5.5f);
        features["Skew_Microsac_Ampl"] = UnityEngine.Random.Range(-1.5f, 1.5f);
        features["Mean_Microsac_Dir"] = UnityEngine.Random.Range(0f, 360f);
        features["SD_Microsac_Dir"] = UnityEngine.Random.Range(10f, 60f);
        features["Max_Microsac_Dir"] = UnityEngine.Random.Range(0f, 360f);
        features["Mean_Microsac_H_Amp"] = UnityEngine.Random.Range(0.5f, 3.5f);
        features["Skew_Microsac_H_Amp"] = UnityEngine.Random.Range(-1.5f, 1.5f);
        features["Max_Microsac_H_Amp"] = UnityEngine.Random.Range(1f, 6f);
        features["Mean_Microsac_V_Amp"] = UnityEngine.Random.Range(0.5f, 3.5f);
        features["SD_Microsac_V_Amp"] = UnityEngine.Random.Range(0.3f, 2.3f);
        features["Skew_Microsac_V_Amp"] = UnityEngine.Random.Range(-1.5f, 1.5f);
        features["Max_Microsac_V_Amp"] = UnityEngine.Random.Range(1f, 6f);
    }
    
    /// <summary>
    /// GSR özellikleri için random değerler üretir
    /// </summary>
    private void GenerateRandomGSRFeatures()
    {
        features["Mean"] = UnityEngine.Random.Range(1f, 11f);
        features["SD"] = UnityEngine.Random.Range(0.5f, 5.5f);
        features["Variance"] = UnityEngine.Random.Range(0.01f, 30f);
        features["Minimum"] = UnityEngine.Random.Range(0.5f, 8f);
        features["Maximum"] = UnityEngine.Random.Range(3f, 15f);
        features["Number of Peaks"] = UnityEngine.Random.Range(5f, 35f);
        features["Number of Valleys"] = UnityEngine.Random.Range(5f, 35f);
        features["Ratio"] = UnityEngine.Random.Range(0.1f, 2f);
    }
    
    /// <summary>
    /// Eye tracking özellikleri hesaplandığında çağrılır
    /// NOT: Server'a gönderilmez - sadece features dictionary'sine eklenir
    /// Server'a gönderim GSR veri akışı başladığında başlar
    /// </summary>
    private void OnEyeTrackingFeaturesCalculated(EyeTrackingFeatures features)
    {
        // Eye tracking özelliklerini features dictionary'sine ekle
        // Ancak server'a gönderme - GSR veri akışı başlamadan gönderilmez
        var eyeTrackingDict = features.ToDictionary();
        SetFeatures(eyeTrackingDict);
        
        // GSR veri akışı başlamadıysa, server'a gönderme
        if (!isGSRDataCollectionStarted)
        {
            if (showDebugLogs)
                Debug.Log("[StressPredictor] Eye tracking verileri toplandı, ancak GSR veri akışı başlamadığı için server'a gönderilmedi.");
            return;
        }
    }
    
    /// <summary>
    /// GSR özellikleri hesaplandığında çağrılır
    /// NOT: Server'a gönderilmez - sadece features dictionary'sine eklenir
    /// Server'a gönderim AutoPredictCoroutine() tarafından yapılır
    /// </summary>
    private void OnGSRFeaturesCalculated(GSRFeatures features)
    {
        // GSR özelliklerini features dictionary'sine ekle
        // Server'a gönderim AutoPredictCoroutine() tarafından yapılır
        var gsrDict = features.ToDictionary();
        SetFeatures(gsrDict);
    }
    
    /// <summary>
    /// GSR veri toplama başladığında çağrılır
    /// </summary>
    private void OnGSRDataCollectionStarted()
    {
        if (isGSRDataCollectionStarted)
        {
            if (showDebugLogs)
                Debug.LogWarning("[StressPredictor] GSR veri toplama zaten başlatılmış!");
            return;
        }
        
        isGSRDataCollectionStarted = true;
        
        if (showDebugLogs)
            Debug.Log("[StressPredictor] GSR veri toplama başladı! Eye tracking ve GSR verileri birleştirilerek server'a gönderilecek.");
        
        StartAutoPredictIfEnabled();
        SendCombinedDataToServer();
    }
    
    /// <summary>
    /// Otomatik tahmin coroutine'ini başlatır (eğer etkinse)
    /// </summary>
    private void StartAutoPredictIfEnabled()
    {
        if (autoPredictEnabled && autoPredictCoroutine == null)
        {
            autoPredictCoroutine = StartCoroutine(AutoPredictCoroutine());
            if (showDebugLogs)
                Debug.Log($"[StressPredictor] Otomatik tahmin başlatıldı (her {predictionInterval}ms)");
        }
    }
    
    /// <summary>
    /// Modelin beklediği özellik listesi (feature_columns.json ile aynı sırada)
    /// v3 model: MI bazlı özellik seçimi sonrası 16 özellik (14 Eye Tracking + 2 GSR)
    /// Log dönüşüm sunucu tarafında otomatik uygulanır, Unity ham değerleri gönderir.
    /// </summary>
    private static readonly string[] ModelExpectedFeatures = new string[]
    {
        // Eye Tracking özellikleri (14 özellik)
        "Number of Peaks",        // GSR - en bilgilendirici özellik (MI=0.178)
        "SD_Saccade_Direction",   // Sakkad yönü standart sapması
        "Mean_Blink_Duration",    // Ortalama göz kırpma süresi
        "Num_of_Blink",           // Göz kırpma sayısı
        "Skew_Microsac_V_Amp",    // Mikrosakkad dikey genlik çarpıklığı
        "Max_Microsac_Dir",       // Mikrosakkad max yön
        "Max_Saccade_Direction",  // Sakkad max yön
        "Mean_Microsac_H_Amp",    // Mikrosakkad yatay genlik ortalaması
        "Num_of_Fixations",       // Fiksasyon sayısı
        "Mean_Saccade_Direction", // Ortalama sakkad yönü
        "SD_Microsac_V_Amp",      // Mikrosakkad dikey genlik std
        "Skew_Saccade_Direction", // Sakkad yönü çarpıklığı
        "Mean_Saccade_Duration",  // Ortalama sakkad süresi
        "SD",                     // GSR sinyali standart sapması
        "Max_Microsac_V_Amp",     // Mikrosakkad dikey genlik max
        "Skew_Fixation_Duration"  // Fiksasyon süresi çarpıklığı
    };
    
    /// <summary>
    /// Eye tracking ve GSR verilerini birleştirerek server'a gönder
    /// </summary>
    private void SendCombinedDataToServer()
    {
        tempFeaturesBuffer.Clear();
        
        CollectEyeTrackingFeaturesForCombined();
        CollectGSRFeaturesForCombined();
        PrepareFeaturesForModel();
        
        LogCombinedFeatures();
        
        if (features.Count > 0)
        {
            if (showDebugLogs)
                Debug.Log($"[StressPredictor] Toplam {features.Count} özellik modelin beklediği sırada hazırlandı, server'a gönderiliyor...");
            
            PredictStress(useRandomValues: false);
        }
        else
        {
            Debug.LogWarning("[StressPredictor] Gönderilecek veri yok! Eye tracking veya GSR verileri henüz hazır değil.");
        }
    }
    
    /// <summary>
    /// Birleştirilmiş veri için eye tracking özelliklerini toplar
    /// </summary>
    private void CollectEyeTrackingFeaturesForCombined()
    {
        if (!useEyeTracking || eyeTrackingCollector == null)
            return;
        
        var eyeFeatures = eyeTrackingCollector.GetFeatures();
        var eyeTrackingDict = eyeFeatures.ToDictionary();
        
        foreach (var kvp in eyeTrackingDict)
        {
            tempFeaturesBuffer[kvp.Key] = kvp.Value;
        }
        
        if (showDebugLogs)
            Debug.Log($"[StressPredictor] Eye tracking özellikleri eklendi: {eyeTrackingDict.Count} özellik");
    }
    
    /// <summary>
    /// Birleştirilmiş veri için GSR özelliklerini toplar
    /// </summary>
    private void CollectGSRFeaturesForCombined()
    {
        if (!useGSR || gsrCollector == null)
            return;
        
        var gsrFeatures = gsrCollector.GetFeatures();
        var gsrDict = gsrFeatures.ToDictionary();
        
        foreach (var kvp in gsrDict)
        {
            tempFeaturesBuffer[kvp.Key] = kvp.Value;
        }
        
        if (showDebugLogs)
            Debug.Log($"[StressPredictor] GSR özellikleri eklendi: {gsrDict.Count} özellik");
    }
    
    /// <summary>
    /// Birleştirilmiş özellikleri loglar
    /// </summary>
    private void LogCombinedFeatures()
    {
        if (!showDebugLogs)
            return;
        
        int missingCount = 0;
        foreach (var featureName in ModelExpectedFeatures)
        {
            if (!tempFeaturesBuffer.ContainsKey(featureName))
                missingCount++;
        }
        
        Debug.Log($"[StressPredictor] Model için hazırlanan özellik sayısı: {features.Count} (Eksik: {missingCount})");
        
        if (missingCount > 0)
            Debug.LogWarning($"[StressPredictor] {missingCount} özellik eksik, 0 ile dolduruldu.");
    }
    
    /// <summary>
    /// Otomatik olarak belirli aralıklarla PredictStress metodunu çağırır
    /// </summary>
    private IEnumerator AutoPredictCoroutine()
    {
        // GSR veri akışı için timeout ile bekle (sonsuz beklemeyi önle)
        float gsrWaitStartTime = Time.time;
        const float GSR_WAIT_TIMEOUT_SECONDS = 120f; // 2 dakika timeout

        while (!isGSRDataCollectionStarted)
        {
            if (Time.time - gsrWaitStartTime > GSR_WAIT_TIMEOUT_SECONDS)
            {
                Debug.LogWarning($"[StressPredictor] GSR veri akışı {GSR_WAIT_TIMEOUT_SECONDS}s içinde başlamadı. " +
                    "Sadece Eye Tracking verileriyle tahmin yapılacak.");
                break;
            }
            yield return new WaitForSeconds(AUTO_PREDICT_WAIT_SECONDS);
        }

        while (autoPredictEnabled)
        {
            yield return new WaitForSeconds(predictionInterval / MILLISECONDS_TO_SECONDS);
            PredictStress(useRandomValues: false);
        }

        autoPredictCoroutine = null;
    }
    
    /// <summary>
    /// Stres tahmini yap
    /// </summary>
    /// <param name="useRandomValues">Eğer true ise, her seferinde yeni random değerler üret</param>
    public async void PredictStress(bool useRandomValues = false)
    {
        if (!CanSendPrediction(useRandomValues))
            return;
        
        if (!await EnsureConnection())
            return;
        
        CollectFeatures(useRandomValues);
        PrepareFeaturesForModel();
        
        if (features.Count == 0)
        {
            Debug.LogWarning("[StressPredictor] Özellik verisi yok! Random değerler için PredictStress(true) kullanın.");
            return;
        }
        
        await SendFeaturesToServer();
    }
    
    /// <summary>
    /// Tahmin gönderilebilir mi kontrol eder
    /// </summary>
    private bool CanSendPrediction(bool useRandomValues)
    {
        if (useGSR && !isGSRDataCollectionStarted && !useRandomValues)
        {
            if (showDebugLogs)
                Debug.Log("[StressPredictor] GSR veri akışı başlamadığı için server'a veri gönderilmedi.");
            return false;
        }
        return true;
    }
    
    /// <summary>
    /// WebSocket bağlantısının açık olduğundan emin olur
    /// </summary>
    private async Task<bool> EnsureConnection()
    {
        if (IsWebSocketOpen())
            return true;
        
        Debug.LogWarning("[StressPredictor] WebSocket bağlantısı açık değil! Yeniden bağlanılıyor...");
        await Connect();
        await Task.Delay((int)RECONNECT_DELAY_MS);
        
        if (!IsWebSocketOpen())
        {
            Debug.LogError("[StressPredictor] Bağlantı kurulamadı!");
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Özellikleri toplar (eye tracking, GSR veya random)
    /// </summary>
    private void CollectFeatures(bool useRandomValues)
    {
        tempFeaturesBuffer.Clear();
        
        if (useRandomValues)
        {
            GenerateRandomFeatures();
            foreach (var kvp in features)
            {
                tempFeaturesBuffer[kvp.Key] = kvp.Value;
            }
        }
        else
        {
            CollectEyeTrackingFeatures();
            CollectGSRFeatures();
        }
    }
    
    /// <summary>
    /// Eye tracking özelliklerini toplar
    /// </summary>
    private void CollectEyeTrackingFeatures()
    {
        if (!useEyeTracking || eyeTrackingCollector == null)
            return;

        EyeTrackingFeatures eyeFeatures;
        lock (_featureLock)
        {
            eyeFeatures = eyeTrackingCollector.GetFeatures();
        }
        var eyeTrackingDict = eyeFeatures.ToDictionary();

        foreach (var kvp in eyeTrackingDict)
        {
            tempFeaturesBuffer[kvp.Key] = kvp.Value;
        }
    }
    
    /// <summary>
    /// GSR özelliklerini toplar
    /// </summary>
    private void CollectGSRFeatures()
    {
        if (!useGSR || gsrCollector == null)
            return;

        GSRFeatures gsrFeatures;
        lock (_featureLock)
        {
            gsrFeatures = gsrCollector.GetFeatures();
        }
        var gsrDict = gsrFeatures.ToDictionary();

        foreach (var kvp in gsrDict)
        {
            tempFeaturesBuffer[kvp.Key] = kvp.Value;
        }
    }
    
    /// <summary>
    /// Modelin beklediği özellikleri hazırlar
    /// </summary>
    private void PrepareFeaturesForModel()
    {
        features.Clear();
        int missingCount = 0;
        
        foreach (var featureName in ModelExpectedFeatures)
        {
            if (tempFeaturesBuffer.ContainsKey(featureName))
            {
                features[featureName] = tempFeaturesBuffer[featureName];
            }
            else
            {
                features[featureName] = 0f;
                missingCount++;
            }
        }
        
        if (missingCount > 0)
        {
            // Eksik özellikler her zaman loglanmalı (veri kalitesi sorunu)
            var missing = new System.Text.StringBuilder();
            missing.Append($"[StressPredictor] {missingCount} özellik eksik (0 ile dolduruldu): ");
            foreach (var featureName in ModelExpectedFeatures)
            {
                if (!tempFeaturesBuffer.ContainsKey(featureName))
                    missing.Append(featureName).Append(", ");
            }
            Debug.LogWarning(missing.ToString());
        }
    }
    
    /// <summary>
    /// Özellikleri server'a gönderir
    /// </summary>
    private async Task SendFeaturesToServer()
    {
        string json = DictionaryToJson(features);
        
        if (showDebugLogs)
        {
            Debug.Log($"[StressPredictor] Server'a gönderilecek özellik sayısı: {features.Count}");
            Debug.Log($"[StressPredictor] JSON uzunluğu: {json.Length} karakter");
        }

        if (!IsWebSocketOpen())
        {
            Debug.LogWarning("[StressPredictor] WebSocket bağlantısı kapalı, veri gönderilemedi.");
            return;
        }

        try
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(json);
            await websocket.SendAsync(
                new ArraySegment<byte>(messageBytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
            
            if (showDebugLogs)
                Debug.Log($"[StressPredictor] Veriler server'a gönderildi ({features.Count} özellik)");
        }
        catch (Exception e)
        {
            Debug.LogError($"[StressPredictor] Mesaj gönderme hatası: {e.Message}");
            OnError?.Invoke($"Mesaj gönderme hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// Dictionary'yi JSON string'e çevir
    /// </summary>
    private string DictionaryToJson(Dictionary<string, float> dict)
    {
        // GC optimizasyonu: StringBuilder buffer'ını yeniden kullan
        jsonBuilderBuffer.Clear();
        jsonBuilderBuffer.Append('{');
        
        bool first = true;
        foreach (var kvp in dict)
        {
            if (!first)
            {
                jsonBuilderBuffer.Append(", ");
            }
            first = false;
            
            // Sayısal değerleri doğru formatta ekle
            jsonBuilderBuffer.Append('"');
            jsonBuilderBuffer.Append(kvp.Key);
            jsonBuilderBuffer.Append("\": ");
            jsonBuilderBuffer.Append(kvp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        
        jsonBuilderBuffer.Append('}');
        return jsonBuilderBuffer.ToString();
    }
    
    /// <summary>
    /// Bağlantı durumunu kontrol et
    /// </summary>
    public bool IsConnected()
    {
        return websocket != null && websocket.State == WebSocketState.Open;
    }
    
    void OnApplicationQuit()
    {
        // Senkron cleanup - .Wait() deadlock'a neden oluyordu!
        CleanupAll();
    }
    
    void OnDestroy()
    {
        // async void kaldırıldı - Unity lifecycle metodları async olmamalı!
        CleanupAll();
    }
    
    void OnDisable()
    {
        autoPredictEnabled = false; // Otomatik tahmini durdur
        isGSRDataCollectionStarted = false; // Flag'i sıfırla
        
        // Otomatik tahmin coroutine'ini durdur
        if (autoPredictCoroutine != null)
        {
            StopCoroutine(autoPredictCoroutine);
            autoPredictCoroutine = null;
        }
        
        StopAllCoroutines(); // Tüm coroutine'leri durdur
        
        // Event subscription'ları temizle
        if (eyeTrackingCollector != null)
        {
            eyeTrackingCollector.OnFeaturesCalculated -= OnEyeTrackingFeaturesCalculated;
        }
        
        if (gsrCollector != null)
        {
            gsrCollector.OnFeaturesCalculated -= OnGSRFeaturesCalculated;
            gsrCollector.OnDataCollectionStarted -= OnGSRDataCollectionStarted;
        }
    }
    
    /// <summary>
    /// Tüm kaynakları senkron olarak temizle (deadlock'tan kaçınmak için)
    /// </summary>
    private void CleanupAll()
    {
        autoPredictEnabled = false;
        isGSRDataCollectionStarted = false;
        isConnected = false;
        wasCalibrated = false;  // Kalibrasyon durumunu sıfırla
        
        // Coroutine'leri durdur
        if (autoPredictCoroutine != null)
        {
            StopCoroutine(autoPredictCoroutine);
            autoPredictCoroutine = null;
        }
        StopAllCoroutines();
        
        // Event subscription'ları temizle
        if (eyeTrackingCollector != null)
        {
            eyeTrackingCollector.OnFeaturesCalculated -= OnEyeTrackingFeaturesCalculated;
        }
        
        if (gsrCollector != null)
        {
            gsrCollector.OnFeaturesCalculated -= OnGSRFeaturesCalculated;
            gsrCollector.OnDataCollectionStarted -= OnGSRDataCollectionStarted;
        }
        
        // WebSocket'i senkron olarak kapat
        CleanupWebSocket();
    }
    
    /// <summary>
    /// WebSocket bağlantısını senkron olarak temizle (deadlock'tan kaçınmak için)
    /// </summary>
    private void CleanupWebSocket()
    {
        // CancellationTokenSource'u bir kez al ve null'la (double-dispose önleme)
        var cts = cancellationTokenSource;
        cancellationTokenSource = null;

        var ws = websocket;
        websocket = null;

        // Önce cancellation token'ı iptal et
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }

        // WebSocket'i kapat
        if (ws != null)
        {
            try { ws.Abort(); } catch { }
            try { ws.Dispose(); } catch { }
        }

        receiveTask = null;
    }
}

/// <summary>
/// Sunucudan gelen tahmin sonucu
/// </summary>
[Serializable]
public class StressResult
{
    public int stres;                    // 0 veya 1
    public float stres_olasilik;         // Stres olma olasılığı (0-1)
    public float stres_yok_olasilik;    // Stres yok olasılığı (0-1)
    public string durum;                 // "stresli" veya "normal"
    public bool basari;                   // İşlem başarılı mı?
    public string error;                 // Hata mesajı (varsa)
    public CalibrationInfo kalibrasyon;  // Kalibrasyon bilgileri
}

/// <summary>
/// Kalibrasyon durumu bilgisi
/// </summary>
[Serializable]
public class CalibrationInfo
{
    public float calibration_progress;   // Geçen süre (saniye)
    public float calibration_total;      // Toplam kalibrasyon süresi (saniye)
    public bool is_calibrated;           // Kalibrasyon tamamlandı mı?
    public float baseline;               // Kişisel baseline (sakin hal stres olasılığı)
    public float threshold;              // Dinamik threshold
}

/*
 * KULLANIM ÖRNEĞİ:
 * 
 * // Inspector'da veya başka bir script'te:
 * 
 * StressPredictor predictor = GetComponent<StressPredictor>();
 * 
 * // Özellikleri ayarla
 * predictor.SetFeature("Num_of_Fixations", 45.2f);
 * predictor.SetFeature("Mean_Fixation_Duration", 234.5f);
 * // ... diğer özellikler
 * 
 * // Event dinle
 * predictor.OnPredictionReceived += (result) => {
 *     Debug.Log($"Stres durumu: {result.durum}");
 *     Debug.Log($"Olasılık: {result.stres_olasilik:P2}");
 * };
 * 
 * // Tahmin yap (manuel özelliklerle)
 * predictor.PredictStress();
 * 
 * // Veya random değerlerle test et
 * predictor.PredictStress(useRandomValues: true);
 * 
 * // Veya önce random değerler üret, sonra tahmin yap
 * predictor.GenerateRandomFeatures();
 * predictor.PredictStress();
 * 
 * // Bağlantı durumunu kontrol et
 * if (predictor.IsConnected())
 * {
 *     Debug.Log("Bağlantı aktif!");
 * }
 */

