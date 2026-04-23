using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Eye tracking ve EDA verilerini CSV formatında kaydeden script
/// Modele gönderilen tüm verileri loglar
/// </summary>
public class DataLogger : MonoBehaviour
{
    [Header("Kayıt Ayarları")]
    [Tooltip("Veri kaydını etkinleştir")]
    public bool enableLogging = true;
    
    [Tooltip("CSV dosyalarının kaydedileceği klasör (Application.persistentDataPath içinde)")]
    public string outputFolder = "StressLensData";
    
    [Tooltip("Her oturum için benzersiz dosya adı oluştur")]
    public bool useSessionTimestamp = true;
    
    [Header("Kayıt Seçenekleri")]
    [Tooltip("Eye tracking ham verilerini kaydet")]
    public bool logRawEyeTracking = true;
    
    [Tooltip("EDA ham verilerini kaydet")]
    public bool logRawEDA = true;
    
    [Tooltip("Modele gönderilen özellikleri kaydet")]
    public bool logModelFeatures = true;
    
    [Tooltip("Model tahmin sonuçlarını kaydet")]
    public bool logPredictionResults = true;
    
    [Header("Veri Kaynakları")]
    public EyeTrackingDataCollector eyeTrackingCollector;
    public GSRDataCollector gsrCollector;
    public StressPredictor stressPredictor;
    
    [Header("Debug")]
    public bool showDebugLogs = true;
    
    // Dosya yolları
    private string eyeTrackingFilePath;
    private string edaFilePath;
    private string modelFeaturesFilePath;
    private string predictionsFilePath;
    private string sessionPath;
    
    // Bellekte veri toplama (play mode'dan çıkınca CSV'ye yazılacak)
    private List<string> eyeTrackingData = new List<string>();
    private List<string> edaData = new List<string>();
    private List<string> modelFeaturesData = new List<string>();
    private List<string> predictionsData = new List<string>();
    
    // Oturum bilgileri
    private string sessionId;
    private float sessionStartTime;
    private bool isInitialized = false;
    
    // Son kaydedilen özellikler (tekrar kaydı önlemek için)
    private float lastEyeTrackingLogTime = 0f;
    private float lastEDALogTime = 0f;
    private float lastModelFeaturesLogTime = 0f;
    
    // GC optimizasyonu için yeniden kullanılabilir StringBuilder (object pooling)
    private StringBuilder stringBuilderBuffer = new StringBuilder(2048);
    
    [Tooltip("Minimum kayıt aralığı (saniye) - çok sık kayıt önleme")]
    public float minimumLogInterval = 0.1f; // 100ms
    
    void Start()
    {
        InitializeLogging();
        
        // Veri kaynaklarını bul
        FindDataSources();
        
        // Event'lere abone ol
        SubscribeToEvents();
    }
    
    void OnDestroy()
    {
        // Play mode'dan çıkınca tüm verileri CSV'ye kaydet
        SaveAllDataToCSV();
        UnsubscribeFromEvents();
    }
    
    void OnApplicationQuit()
    {
        // Uygulama kapanırken tüm verileri CSV'ye kaydet
        SaveAllDataToCSV();
    }
    
    /// <summary>
    /// Logging sistemini başlat
    /// </summary>
    private void InitializeLogging()
    {
        if (!enableLogging)
        {
            if (showDebugLogs)
                Debug.Log("[DataLogger] Logging devre dışı");
            return;
        }
        
        // Oturum ID oluştur
        sessionId = useSessionTimestamp 
            ? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") 
            : "session";
        sessionStartTime = Time.time;
        
        // Çıktı klasörünü oluştur
        string basePath = Path.Combine(Application.persistentDataPath, outputFolder);
        if (!Directory.Exists(basePath))
        {
            Directory.CreateDirectory(basePath);
        }
        
        // Oturum klasörünü oluştur
        sessionPath = Path.Combine(basePath, sessionId);
        if (!Directory.Exists(sessionPath))
        {
            Directory.CreateDirectory(sessionPath);
        }
        
        // Dosya yollarını ayarla
        eyeTrackingFilePath = Path.Combine(sessionPath, "eye_tracking_raw.csv");
        edaFilePath = Path.Combine(sessionPath, "eda_raw.csv");
        modelFeaturesFilePath = Path.Combine(sessionPath, "model_features.csv");
        predictionsFilePath = Path.Combine(sessionPath, "predictions.csv");
        
        // Veri listelerini temizle
        eyeTrackingData.Clear();
        edaData.Clear();
        modelFeaturesData.Clear();
        predictionsData.Clear();
        
        isInitialized = true;
        
        if (showDebugLogs)
        {
            Debug.Log($"[DataLogger] Logging başlatıldı (veriler bellekte toplanacak)");
            Debug.Log($"[DataLogger] Play mode'dan çıkınca kaydedilecek klasör: {sessionPath}");
        }
    }
    
    /// <summary>
    /// Bellekteki tüm verileri CSV dosyalarına yaz (play mode'dan çıkınca çağrılır)
    /// </summary>
    private void SaveAllDataToCSV()
    {
        if (!enableLogging || !isInitialized)
        {
            if (showDebugLogs)
                Debug.Log("[DataLogger] Kaydedilecek veri yok veya logging devre dışı");
            return;
        }
        
        try
        {
            int totalRecords = 0;
            
            // Eye Tracking Raw CSV
            if (logRawEyeTracking && eyeTrackingData.Count > 0)
            {
                using (StreamWriter writer = new StreamWriter(eyeTrackingFilePath, false, Encoding.UTF8))
                {
                    writer.WriteLine(GetEyeTrackingRawHeader());
                    foreach (string line in eyeTrackingData)
                    {
                        writer.WriteLine(line);
                    }
                }
                totalRecords += eyeTrackingData.Count;
                
                if (showDebugLogs)
                    Debug.Log($"[DataLogger] Eye tracking CSV kaydedildi: {eyeTrackingData.Count} kayıt -> {eyeTrackingFilePath}");
            }
            
            // EDA Raw CSV
            if (logRawEDA && edaData.Count > 0)
            {
                using (StreamWriter writer = new StreamWriter(edaFilePath, false, Encoding.UTF8))
                {
                    writer.WriteLine(GetEDARawHeader());
                    foreach (string line in edaData)
                    {
                        writer.WriteLine(line);
                    }
                }
                totalRecords += edaData.Count;
                
                if (showDebugLogs)
                    Debug.Log($"[DataLogger] EDA CSV kaydedildi: {edaData.Count} kayıt -> {edaFilePath}");
            }
            
            // Model Features CSV
            if (logModelFeatures && modelFeaturesData.Count > 0)
            {
                using (StreamWriter writer = new StreamWriter(modelFeaturesFilePath, false, Encoding.UTF8))
                {
                    writer.WriteLine(GetModelFeaturesHeader());
                    foreach (string line in modelFeaturesData)
                    {
                        writer.WriteLine(line);
                    }
                }
                totalRecords += modelFeaturesData.Count;
                
                if (showDebugLogs)
                    Debug.Log($"[DataLogger] Model features CSV kaydedildi: {modelFeaturesData.Count} kayıt -> {modelFeaturesFilePath}");
            }
            
            // Predictions CSV
            if (logPredictionResults && predictionsData.Count > 0)
            {
                using (StreamWriter writer = new StreamWriter(predictionsFilePath, false, Encoding.UTF8))
                {
                    writer.WriteLine(GetPredictionsHeader());
                    foreach (string line in predictionsData)
                    {
                        writer.WriteLine(line);
                    }
                }
                totalRecords += predictionsData.Count;
                
                if (showDebugLogs)
                    Debug.Log($"[DataLogger] Predictions CSV kaydedildi: {predictionsData.Count} kayıt -> {predictionsFilePath}");
            }
            
            Debug.Log($"[DataLogger] ✅ Tüm veriler kaydedildi! Toplam {totalRecords} kayıt -> {sessionPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[DataLogger] CSV kaydetme hatası: {e.Message}\n{e.StackTrace}");
        }
    }
    
    /// <summary>
    /// Veri kaynaklarını bul
    /// </summary>
    private void FindDataSources()
    {
        if (eyeTrackingCollector == null)
            eyeTrackingCollector = FindAnyObjectByType<EyeTrackingDataCollector>();
        
        if (gsrCollector == null)
            gsrCollector = FindAnyObjectByType<GSRDataCollector>();
        
        if (stressPredictor == null)
            stressPredictor = FindAnyObjectByType<StressPredictor>();
        
        if (showDebugLogs)
        {
            Debug.Log($"[DataLogger] Eye Tracking Collector: {(eyeTrackingCollector != null ? "Bulundu" : "Bulunamadı")}");
            Debug.Log($"[DataLogger] GSR Collector: {(gsrCollector != null ? "Bulundu" : "Bulunamadı")}");
            Debug.Log($"[DataLogger] Stress Predictor: {(stressPredictor != null ? "Bulundu" : "Bulunamadı")}");
        }
    }
    
    /// <summary>
    /// Event'lere abone ol
    /// </summary>
    private void SubscribeToEvents()
    {
        if (eyeTrackingCollector != null)
        {
            eyeTrackingCollector.OnFeaturesCalculated += OnEyeTrackingFeaturesReceived;
        }
        
        if (gsrCollector != null)
        {
            gsrCollector.OnFeaturesCalculated += OnGSRFeaturesReceived;
        }
        
        if (stressPredictor != null)
        {
            stressPredictor.OnPredictionReceived += OnPredictionReceived;
        }
    }
    
    /// <summary>
    /// Event aboneliklerini iptal et
    /// </summary>
    private void UnsubscribeFromEvents()
    {
        if (eyeTrackingCollector != null)
        {
            eyeTrackingCollector.OnFeaturesCalculated -= OnEyeTrackingFeaturesReceived;
        }
        
        if (gsrCollector != null)
        {
            gsrCollector.OnFeaturesCalculated -= OnGSRFeaturesReceived;
        }
        
        if (stressPredictor != null)
        {
            stressPredictor.OnPredictionReceived -= OnPredictionReceived;
        }
    }
    
    #region CSV Headers
    
    private string GetEyeTrackingRawHeader()
    {
        return "Timestamp,SessionTime,Num_of_Fixations,Mean_Fixation_Duration,SD_Fixation_Duration,Skew_Fixation_Duration,Max_Fixation_Duration,First_Fixation_Duration," +
               "Num_of_Saccade,Mean_Saccade_Duration,SD_Saccade_Duration,Skew_Saccade_Duration,Max_Saccade_Duration," +
               "Mean_Saccade_Amplitude,SD_Saccade_Amplitude,Skew_Saccade_Amplitude,Max_Saccade_Amplitude," +
               "Mean_Saccade_Direction,SD_Saccade_Direction,Skew_Saccade_Direction,Max_Saccade_Direction," +
               "Mean_Saccade_Length,SD_Saccade_Length,Skew_Saccade_Length,Max_Saccade_Length," +
               "Num_of_Blink,Mean_Blink_Duration,SD_Blink_Duration,Skew_Blink_Duration,Max_Blink_Duration," +
               "Num_of_Microsac,Mean_Microsac_Peak_Vel,SD_Microsac_Peak_Vel,Skew_Microsac_Peak_Vel,Max_Microsac_Peak_Vel," +
               "Mean_Microsac_Ampl,SD_Microsac_Ampl,Skew_Microsac_Ampl,Max_Microsac_Ampl," +
               "Mean_Microsac_Dir,SD_Microsac_Dir,Skew_Microsac_Dir,Max_Microsac_Dir," +
               "Mean_Microsac_H_Amp,SD_Microsac_H_Amp,Skew_Microsac_H_Amp,Max_Microsac_H_Amp," +
               "Mean_Microsac_V_Amp,SD_Microsac_V_Amp,Skew_Microsac_V_Amp,Max_Microsac_V_Amp";
    }
    
    private string GetEDARawHeader()
    {
        return "Timestamp,SessionTime,Mean,SD,Variance,Minimum,Maximum,Number of Peaks,Number of Valleys,Ratio";
    }
    
    private string GetModelFeaturesHeader()
    {
        // Modelin beklediği 32 özellik (feature_columns.json sırası)
        return "Timestamp,SessionTime," +
               "Num_of_Fixations,Mean_Fixation_Duration,Skew_Fixation_Duration,First_Fixation_Duration," +
               "Mean_Saccade_Duration,Skew_Saccade_Duration,Skew_Saccade_Amplitude,Mean_Saccade_Direction,SD_Saccade_Direction,Max_Saccade_Direction," +
               "Num_of_Blink,Mean_Blink_Duration,Skew_Blink_Duration," +
               "Num_of_Microsac,Mean_Microsac_Peak_Vel,SD_Microsac_Peak_Vel,Skew_Microsac_Peak_Vel," +
               "SD_Microsac_Ampl,Skew_Microsac_Ampl,Mean_Microsac_Dir,SD_Microsac_Dir,Max_Microsac_Dir," +
               "Mean_Microsac_H_Amp,Skew_Microsac_H_Amp,Max_Microsac_H_Amp," +
               "Mean_Microsac_V_Amp,SD_Microsac_V_Amp,Skew_Microsac_V_Amp,Max_Microsac_V_Amp," +
               "Mean,SD,Number of Peaks";
    }
    
    private string GetPredictionsHeader()
    {
        return "Timestamp,SessionTime,Stress,Stress_Probability,NoStress_Probability,Status";
    }
    
    #endregion
    
    #region Event Handlers
    
    /// <summary>
    /// Eye tracking özellikleri alındığında
    /// </summary>
    private void OnEyeTrackingFeaturesReceived(EyeTrackingFeatures features)
    {
        if (!enableLogging || !isInitialized || !logRawEyeTracking)
            return;
        
        // Minimum aralık kontrolü
        if (Time.time - lastEyeTrackingLogTime < minimumLogInterval)
            return;
        
        lastEyeTrackingLogTime = Time.time;
        
        LogEyeTrackingData(features);
    }
    
    /// <summary>
    /// GSR/EDA özellikleri alındığında
    /// </summary>
    private void OnGSRFeaturesReceived(GSRFeatures features)
    {
        if (showDebugLogs && edaLogCount < 5)
        {
            Debug.Log($"[DataLogger] GSR Features alındı - enableLogging: {enableLogging}, isInitialized: {isInitialized}, logRawEDA: {logRawEDA}");
        }
        
        if (!enableLogging || !isInitialized || !logRawEDA)
            return;
        
        // Minimum aralık kontrolü
        if (Time.time - lastEDALogTime < minimumLogInterval)
            return;
        
        lastEDALogTime = Time.time;
        edaLogCount++;
        
        if (showDebugLogs && edaLogCount <= 5)
        {
            Debug.Log($"[DataLogger] EDA verisi CSV'ye yazılıyor #{edaLogCount} - Mean: {features.Mean:F6}");
        }
        
        LogEDAData(features);
    }
    
    private int edaLogCount = 0;
    
    /// <summary>
    /// Model tahmini alındığında
    /// </summary>
    private void OnPredictionReceived(StressResult result)
    {
        if (!enableLogging || !isInitialized)
            return;
        
        // Model özelliklerini kaydet
        if (logModelFeatures)
        {
            LogModelFeatures();
        }
        
        // Tahmin sonucunu kaydet
        if (logPredictionResults)
        {
            LogPrediction(result);
        }
    }
    
    #endregion
    
    #region Logging Methods
    
    /// <summary>
    /// Eye tracking verilerini belleğe kaydet (play mode'dan çıkınca CSV'ye yazılacak)
    /// </summary>
    private void LogEyeTrackingData(EyeTrackingFeatures f)
    {
        if (!isInitialized)
            return;
        
        try
        {
            float sessionTime = Time.time - sessionStartTime;
            
            // GC optimizasyonu: StringBuilder buffer'ını yeniden kullan
            stringBuilderBuffer.Clear();
            stringBuilderBuffer.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',');
            stringBuilderBuffer.Append(sessionTime.ToString("F3")).Append(',');
            // Fixation
            stringBuilderBuffer.Append(f.Num_of_Fixations.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Fixation_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Fixation_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Fixation_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Fixation_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.First_Fixation_Duration.ToString("F4")).Append(',');
            // Saccade
            stringBuilderBuffer.Append(f.Num_of_Saccade.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Saccade_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Saccade_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Saccade_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Saccade_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Saccade_Amplitude.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Saccade_Amplitude.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Saccade_Amplitude.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Saccade_Amplitude.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Saccade_Direction.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Saccade_Direction.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Saccade_Direction.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Saccade_Direction.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Saccade_Length.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Saccade_Length.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Saccade_Length.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Saccade_Length.ToString("F4")).Append(',');
            // Blink
            stringBuilderBuffer.Append(f.Num_of_Blink.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Blink_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Blink_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Blink_Duration.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Blink_Duration.ToString("F4")).Append(',');
            // Microsaccade
            stringBuilderBuffer.Append(f.Num_of_Microsac.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Microsac_Peak_Vel.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Microsac_Peak_Vel.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Microsac_Peak_Vel.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Microsac_Peak_Vel.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Microsac_Ampl.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Microsac_Ampl.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Microsac_Ampl.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Microsac_Ampl.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Microsac_Dir.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Microsac_Dir.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Microsac_Dir.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Microsac_Dir.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Microsac_H_Amp.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Microsac_H_Amp.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Microsac_H_Amp.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Microsac_H_Amp.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Mean_Microsac_V_Amp.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD_Microsac_V_Amp.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Skew_Microsac_V_Amp.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Max_Microsac_V_Amp.ToString("F4"));
            
            eyeTrackingData.Add(stringBuilderBuffer.ToString());
        }
        catch (Exception e)
        {
            Debug.LogError($"[DataLogger] Eye tracking kaydetme hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// EDA verilerini belleğe kaydet (play mode'dan çıkınca CSV'ye yazılacak)
    /// </summary>
    private void LogEDAData(GSRFeatures f)
    {
        if (!isInitialized)
            return;
        
        try
        {
            float sessionTime = Time.time - sessionStartTime;
            
            // GC optimizasyonu: StringBuilder buffer'ını yeniden kullan
            stringBuilderBuffer.Clear();
            stringBuilderBuffer.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',');
            stringBuilderBuffer.Append(sessionTime.ToString("F3")).Append(',');
            stringBuilderBuffer.Append(f.Mean.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.SD.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Variance.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Minimum.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Maximum.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(f.Number_of_Peaks.ToString("F6")).Append(',');
            stringBuilderBuffer.Append(f.Number_of_Valleys.ToString("F6")).Append(',');
            stringBuilderBuffer.Append(f.Ratio.ToString("F4"));
            
            edaData.Add(stringBuilderBuffer.ToString());
        }
        catch (Exception e)
        {
            Debug.LogError($"[DataLogger] EDA kaydetme hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// Modele gönderilen özellikleri belleğe kaydet (play mode'dan çıkınca CSV'ye yazılacak)
    /// </summary>
    private void LogModelFeatures()
    {
        if (!isInitialized)
            return;
        
        // Minimum aralık kontrolü
        if (Time.time - lastModelFeaturesLogTime < minimumLogInterval)
            return;
        
        lastModelFeaturesLogTime = Time.time;
        
        try
        {
            float sessionTime = Time.time - sessionStartTime;
            
            // Eye tracking özelliklerini al
            EyeTrackingFeatures eyeFeatures = null;
            if (eyeTrackingCollector != null)
            {
                eyeFeatures = eyeTrackingCollector.GetFeatures();
            }
            
            // GSR özelliklerini al
            GSRFeatures gsrFeatures = null;
            if (gsrCollector != null)
            {
                gsrFeatures = gsrCollector.GetFeatures();
            }
            
            // GC optimizasyonu: StringBuilder buffer'ını yeniden kullan
            stringBuilderBuffer.Clear();
            stringBuilderBuffer.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',');
            stringBuilderBuffer.Append(sessionTime.ToString("F3")).Append(',');
            // Eye Tracking - Fixation (4)
            stringBuilderBuffer.Append((eyeFeatures?.Num_of_Fixations ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Fixation_Duration ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Fixation_Duration ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.First_Fixation_Duration ?? 0).ToString("F4")).Append(',');
            // Eye Tracking - Saccade (6)
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Saccade_Duration ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Saccade_Duration ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Saccade_Amplitude ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Saccade_Direction ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.SD_Saccade_Direction ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Max_Saccade_Direction ?? 0).ToString("F4")).Append(',');
            // Eye Tracking - Blink (3)
            stringBuilderBuffer.Append((eyeFeatures?.Num_of_Blink ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Blink_Duration ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Blink_Duration ?? 0).ToString("F4")).Append(',');
            // Eye Tracking - Microsaccade (16)
            stringBuilderBuffer.Append((eyeFeatures?.Num_of_Microsac ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Microsac_Peak_Vel ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.SD_Microsac_Peak_Vel ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Microsac_Peak_Vel ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.SD_Microsac_Ampl ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Microsac_Ampl ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Microsac_Dir ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.SD_Microsac_Dir ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Max_Microsac_Dir ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Microsac_H_Amp ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Microsac_H_Amp ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Max_Microsac_H_Amp ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Mean_Microsac_V_Amp ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.SD_Microsac_V_Amp ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Skew_Microsac_V_Amp ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((eyeFeatures?.Max_Microsac_V_Amp ?? 0).ToString("F4")).Append(',');
            // GSR (3)
            stringBuilderBuffer.Append((gsrFeatures?.Mean ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((gsrFeatures?.SD ?? 0).ToString("F4")).Append(',');
            stringBuilderBuffer.Append((gsrFeatures?.Number_of_Peaks ?? 0).ToString("F6"));
            
            modelFeaturesData.Add(stringBuilderBuffer.ToString());
        }
        catch (Exception e)
        {
            Debug.LogError($"[DataLogger] Model features kaydetme hatası: {e.Message}");
        }
    }
    
    /// <summary>
    /// Tahmin sonucunu belleğe kaydet (play mode'dan çıkınca CSV'ye yazılacak)
    /// </summary>
    private void LogPrediction(StressResult result)
    {
        if (!isInitialized)
            return;
        
        try
        {
            float sessionTime = Time.time - sessionStartTime;
            
            // GC optimizasyonu: StringBuilder buffer'ını yeniden kullan
            stringBuilderBuffer.Clear();
            stringBuilderBuffer.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',');
            stringBuilderBuffer.Append(sessionTime.ToString("F3")).Append(',');
            stringBuilderBuffer.Append(result.stres).Append(',');
            stringBuilderBuffer.Append(result.stres_olasilik.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(result.stres_yok_olasilik.ToString("F4")).Append(',');
            stringBuilderBuffer.Append(result.durum);
            
            predictionsData.Add(stringBuilderBuffer.ToString());
            
            if (showDebugLogs)
                Debug.Log($"[DataLogger] Tahmin kaydedildi (bellekte): {result.durum} ({result.stres_olasilik:P2})");
        }
        catch (Exception e)
        {
            Debug.LogError($"[DataLogger] Prediction kaydetme hatası: {e.Message}");
        }
    }
    
    #endregion
    
    /// <summary>
    /// Bellekteki verileri temizle
    /// </summary>
    private void ClearAllData()
    {
        eyeTrackingData.Clear();
        edaData.Clear();
        modelFeaturesData.Clear();
        predictionsData.Clear();
        
        if (showDebugLogs)
            Debug.Log("[DataLogger] Bellekteki tüm veriler temizlendi");
    }
    
    /// <summary>
    /// Bellekte kaç kayıt olduğunu döndür
    /// </summary>
    public int GetTotalRecordCount()
    {
        return eyeTrackingData.Count + edaData.Count + modelFeaturesData.Count + predictionsData.Count;
    }
    
    /// <summary>
    /// Detaylı kayıt sayılarını döndür
    /// </summary>
    public string GetRecordCountsSummary()
    {
        return $"Eye Tracking: {eyeTrackingData.Count}, EDA: {edaData.Count}, Model Features: {modelFeaturesData.Count}, Predictions: {predictionsData.Count}";
    }
    
    /// <summary>
    /// CSV dosyalarının kaydedildiği klasör yolunu döndür
    /// </summary>
    public string GetOutputPath()
    {
        if (string.IsNullOrEmpty(sessionId))
            return "";
        
        return Path.Combine(Application.persistentDataPath, outputFolder, sessionId);
    }
    
    /// <summary>
    /// Manuel olarak mevcut verileri belleğe kaydet
    /// </summary>
    public void ForceLogCurrentData()
    {
        if (!enableLogging || !isInitialized)
            return;
        
        // Eye tracking
        if (logRawEyeTracking && eyeTrackingCollector != null)
        {
            var eyeFeatures = eyeTrackingCollector.GetFeatures();
            LogEyeTrackingData(eyeFeatures);
        }
        
        // EDA
        if (logRawEDA && gsrCollector != null)
        {
            var gsrFeatures = gsrCollector.GetFeatures();
            LogEDAData(gsrFeatures);
        }
        
        // Model features
        if (logModelFeatures)
        {
            LogModelFeatures();
        }
        
        if (showDebugLogs)
            Debug.Log($"[DataLogger] Mevcut veriler belleğe kaydedildi. Toplam: {GetRecordCountsSummary()}");
    }
    
    /// <summary>
    /// Manuel olarak tüm verileri CSV'ye kaydet (play mode'dan çıkmadan)
    /// </summary>
    public void ForceSaveToCSV()
    {
        SaveAllDataToCSV();
    }
}
