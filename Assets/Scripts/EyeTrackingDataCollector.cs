using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VIVE.OpenXR;
using VIVE.OpenXR.EyeTracker;

/// <summary>
/// HTC Vive Focus Vision eye tracking verilerini toplayıp işleyen script
/// VIVE OpenXR Eye Tracker API kullanır
/// </summary>
public class EyeTrackingDataCollector : MonoBehaviour
{
    [Header("Eye Tracking Ayarları")]
    [Tooltip("Eye tracking verilerini toplamayı başlat")]
    public bool startCollecting = false;
    
    [Tooltip("Veri toplama frekansı (Hz)")]
    public float samplingRate = 100f;
    
    [Header("Hesaplama Parametreleri")]
    [Tooltip("Özellik hesaplama için zaman penceresi (saniye) - VREED dataset'ine göre: 30-60 saniye (360-VE videoları 1-3 dakika)")]
    public float timeWindow = 30f;
    
    [Tooltip("Fixation eşik hızı (derece/saniye)")]
    public float fixationVelocityThreshold = 30f;
    
    [Tooltip("Saccade minimum hızı (derece/saniye)")]
    public float saccadeMinVelocity = 50f;
    
    [Tooltip("Microsaccade maksimum genlik (derece)")]
    public float microsaccadeMaxAmplitude = 1.0f;
    
    [Tooltip("Blink tespiti için göz açıklığı eşiği")]
    public float blinkThreshold = 0.1f;
    
    [Header("Göz Rotasyonu Görselleştirme")]
    [Tooltip("Göz rotasyonlarını GameObject'lere uygula")]
    public bool applyEyeRotationToObjects = false;
    
    [Tooltip("Sol göz rotasyonunu uygulayacak GameObject (örn: Sphere)")]
    public GameObject leftEyeRotationTarget;
    
    [Tooltip("Sağ göz rotasyonunu uygulayacak GameObject (örn: Sphere)")]
    public GameObject rightEyeRotationTarget;
    
    [Header("Main Sequence Sağlama")]
    [Tooltip("Hız-genlik (Main Sequence) biyolojik uyumunu doğula; rastgele veri değil gerçek göz hareketi olduğunu kontrol eder")]
    public bool enableMainSequenceValidation = true;
    
    [Tooltip("Main Sequence (amplitude, peakVelocity) çiftlerini logla — grafiğe dökmek için")]
    public bool logMainSequenceDataForPlotting = false;
    
    [Header("Mikrosakkad Sağlama (Engbert-Kliegl, Pisagor, Fiksasyon)")]
    [Tooltip("Mikrosakkad peak velocity'lerin Engbert-Kliegl eşik aralığında olup olmadığını kontrol et")]
    public bool enableMicrosaccadeVelocityValidation = true;
    
    [Tooltip("H ve V bileşenlerinin sqrt(H²+V²)=genlik (Pisagor) verip vermediğini rastgele örneklerle test et")]
    public bool enablePythagoreanValidation = true;
    
    [Tooltip("Mikrosakkad sayısının fiksasyon süreleriyle orantılı olup olmadığını incele")]
    public bool enableMicrosaccadeFixationProportionCheck = true;
    
    [Header("Debug")]
    public bool showDebugLogs = true;
    
    [Tooltip("Ham eye tracking verilerini logla (işlenmemiş HTC verileri)")]
    public bool logRawEyeData = true;
    
    // ========== VERİ TOPLAMA ==========
    private EyeTrackingData _cachedEyeData;
    private List<EyeTrackingSample> samples = new List<EyeTrackingSample>();
    private float lastSampleTime = 0f;
    private float sampleInterval;
    private bool isEyeTrackingAvailable = false;
    
    // ========== HESAPLANAN ÖZELLİKLER ==========
    private EyeTrackingFeatures currentFeatures = new EyeTrackingFeatures();
    public event Action<EyeTrackingFeatures> OnFeaturesCalculated;
    
    // ========== GC OPTİMİZASYONU: YENİDEN KULLANILABİLİR BUFFER'LAR ==========
    private List<float> fixationDurationsBuffer = new List<float>();
    private List<float> saccadeDurationsBuffer = new List<float>();
    private List<float> saccadeAmplitudesBuffer = new List<float>();
    private List<float> saccadePeakVelocitiesBuffer = new List<float>();
    private List<float> saccadeDirectionsBuffer = new List<float>();
    private List<float> saccadeLengthsBuffer = new List<float>();
    private List<float> blinkDurationsBuffer = new List<float>();
    private List<float> microsaccadePeakVelocitiesBuffer = new List<float>();
    private List<float> microsaccadeAmplitudesBuffer = new List<float>();
    private List<float> microsaccadeDirectionsBuffer = new List<float>();
    private List<float> microsaccadeHAmplitudesBuffer = new List<float>();
    private List<float> microsaccadeVAmplitudesBuffer = new List<float>();
    
    // ========== REAL-TIME BLINK TRACKING (SLIDING WINDOW) ==========
    /// <summary>
    /// Blink kaydı - timestamp ile birlikte saklanır (sliding window için)
    /// </summary>
    private struct BlinkRecord
    {
        public float timestamp;   // Blink'in gerçekleştiği zaman (Time.time)
        public float durationMs;  // Blink süresi (milisaniye)
    }
    
    private List<BlinkRecord> blinkRecords = new List<BlinkRecord>();
    private bool isCurrentlyBlinking = false;
    private float blinkStartTimeMs = 0f;
    
    // ========== LOGGING VE CACHE ==========
    private StringBuilder logBuilder = new StringBuilder(4096);
    private float cachedCutoffTime = 0f;
    private int blinkDebugLogCount = 0;
    private int eyeOpennessDebugLogCount = 0;
    
    // Sabitler
    /// <summary>Fiksasyon için minimum süre (saniye). 100ms'den kısa odaklanmalar gürültü kabul edilir ve kaydedilmez.</summary>
    private const float MIN_FIXATION_DURATION_SECONDS = 0.1f;
    private const float MIN_BLINK_DURATION_MS = 20f;
    private const float MAX_BLINK_DURATION_MS = 1000f;
    private const float MIN_BLINK_THRESHOLD = 0.3f;
    private const float MIN_SACCADE_VELOCITY_DEG_PER_SEC = 30f;
    private const float MIN_SACCADE_AMPLITUDE_DEG = 0.5f;
    /// <summary>Bu süreden kısa sakkadlar gürültü kabul edilir; Main Sequence grafiği ve istatistiklere dahil edilmez (ms).</summary>
    private const float MIN_SACCADE_DURATION_MS = 10f;
    private const float MIN_MICROSACCADE_AMPLITUDE_DEG = 0.1f;
    private const float MILLISECONDS_PER_SECOND = 1000f;
    private const float DEFAULT_EYE_OPENNESS = 1f;
    private const int REQUIRED_EYE_COUNT = 2;
    private const int MIN_SAMPLES_FOR_CALCULATION = 2;
    private const int MIN_SAMPLES_FOR_SKEWNESS = 3;
    /// <summary>Main Sequence sağlama: hız-genlik korelasyonu bu eşiğin üzerindeyse biyolojik standartlarla uyumlu kabul edilir.</summary>
    private const float MIN_MAIN_SEQUENCE_CORRELATION = 0.2f;
    private float lastMainSequenceValidationTime = -999f;
    private const float MAIN_SEQUENCE_VALIDATION_INTERVAL = 10f;
    // Engbert & Kliegl: mikrosakkad hız eşikleri (derece/saniye) — literatür tipik 30–150 deg/s
    private const float ENGBERT_MIN_PEAK_VELOCITY_DEG_PER_SEC = 30f;
    private const float ENGBERT_MAX_PEAK_VELOCITY_DEG_PER_SEC = 200f;
    private const float PYTHAGOREAN_TOLERANCE_DEG = 0.02f;
    private const float PYTHAGOREAN_TOLERANCE_RELATIVE = 0.05f;
    private const int PYTHAGOREAN_RANDOM_SAMPLES = 10;
    private float lastMicrosaccadeValidationTime = -999f;
    private const float MICROSACCADE_VALIDATION_INTERVAL = 10f;
    
    void Start()
    {
        sampleInterval = 1f / samplingRate;
        CheckEyeTrackingAvailability();
    }
    
    void Update()
    {
        if (startCollecting && isEyeTrackingAvailable)
        {
            CollectEyeTrackingData();
        }
    }
    
    /// <summary>
    /// Eye tracking özelliğinin mevcut olup olmadığını kontrol eder
    /// </summary>
    private void CheckEyeTrackingAvailability()
    {
        try
        {
            XR_HTC_eye_tracker.Interop.GetEyeGazeData(out XrSingleEyeGazeDataHTC[] testGazes);
            isEyeTrackingAvailable = testGazes != null && testGazes.Length >= REQUIRED_EYE_COUNT;
        }
        catch (Exception e)
        {
            isEyeTrackingAvailable = false;
            Debug.LogError($"[EyeTrackingDataCollector] Eye tracking mevcut değil: {e.Message}\n{e.StackTrace}");
        }
    }
    
    /// <summary>
    /// Eye tracking verilerini topla (VIVE OpenXR API kullanarak)
    /// </summary>
    private void CollectEyeTrackingData()
    {
        if (!ShouldCollectSample())
            return;
        
        lastSampleTime = Time.time;
        
        if (!UnityEngine.XR.XRSettings.enabled)
            return;
        
        try
        {
            _cachedEyeData = default;
            RetrieveEyeTrackingData(ref _cachedEyeData);
            if (!_cachedEyeData.IsValid)
                return;

            LogAndVisualizeData(in _cachedEyeData);

            ProcessBlinkDetection(in _cachedEyeData);

            CreateAndStoreSample(in _cachedEyeData);
            
            CleanupOldSamples();
            CleanupOldBlinks();
            
            CalculateFeatures();
        }
        catch (Exception e)
        {
            if (showDebugLogs)
                Debug.LogError($"[EyeTrackingDataCollector] Veri toplama hatası: {e.Message}\n{e.StackTrace}");
        }
    }
    
    /// <summary>
    /// Bu frame'de sample toplanmalı mı kontrol eder
    /// </summary>
    private bool ShouldCollectSample()
    {
        return Time.time - lastSampleTime >= sampleInterval;
    }
    
    /// <summary>
    /// HTC OpenXR API'den eye tracking verilerini alır
    /// </summary>
    private void RetrieveEyeTrackingData(ref EyeTrackingData data)
    {
        // Gaze verilerini al
        XR_HTC_eye_tracker.Interop.GetEyeGazeData(out XrSingleEyeGazeDataHTC[] gazeArray);
        if (gazeArray == null || gazeArray.Length < REQUIRED_EYE_COUNT)
            return;

        data.LeftGaze = gazeArray[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC];
        data.RightGaze = gazeArray[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC];

        // Pupil verilerini al
        XR_HTC_eye_tracker.Interop.GetEyePupilData(out XrSingleEyePupilDataHTC[] pupilArray);
        if (pupilArray != null && pupilArray.Length >= REQUIRED_EYE_COUNT)
        {
            data.LeftPupil = pupilArray[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC];
            data.RightPupil = pupilArray[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC];
        }

        // Geometric verilerini al (eye openness için)
        XR_HTC_eye_tracker.Interop.GetEyeGeometricData(out XrSingleEyeGeometricDataHTC[] geometricArray);
        if (geometricArray != null && geometricArray.Length >= REQUIRED_EYE_COUNT)
        {
            data.LeftGeometric = geometricArray[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC];
            data.RightGeometric = geometricArray[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC];
        }

        // Ortalama gaze pozisyonu ve rotasyonu hesapla
        CalculateAverageGaze(ref data);

        // Göz açıklığı değerlerini hesapla
        data.HasValidGeometricData = data.LeftGeometric.isValid && data.RightGeometric.isValid;
        data.LeftEyeOpenness = data.HasValidGeometricData ? data.LeftGeometric.eyeOpenness : DEFAULT_EYE_OPENNESS;
        data.RightEyeOpenness = data.HasValidGeometricData ? data.RightGeometric.eyeOpenness : DEFAULT_EYE_OPENNESS;

        // Pupil çapı değerlerini hesapla
        data.LeftPupilDiameter = data.LeftPupil.isDiameterValid ? data.LeftPupil.pupilDiameter : 0f;
        data.RightPupilDiameter = data.RightPupil.isDiameterValid ? data.RightPupil.pupilDiameter : 0f;
    }
    
    /// <summary>
    /// Ortalama gaze pozisyonu ve rotasyonunu hesaplar
    /// </summary>
    private void CalculateAverageGaze(ref EyeTrackingData data)
    {
        if (data.LeftGaze.isValid && data.RightGaze.isValid)
        {
            Vector3 leftPos = data.LeftGaze.gazePose.position.ToUnityVector();
            Vector3 rightPos = data.RightGaze.gazePose.position.ToUnityVector();
            data.AverageGazePosition = (leftPos + rightPos) * 0.5f;
            
            Quaternion leftRot = data.LeftGaze.gazePose.orientation.ToUnityQuaternion();
            Quaternion rightRot = data.RightGaze.gazePose.orientation.ToUnityQuaternion();
            data.AverageGazeRotation = Quaternion.Slerp(leftRot, rightRot, 0.5f);
            data.IsValid = true;
        }
        else if (data.LeftGaze.isValid)
        {
            data.AverageGazePosition = data.LeftGaze.gazePose.position.ToUnityVector();
            data.AverageGazeRotation = data.LeftGaze.gazePose.orientation.ToUnityQuaternion();
            data.IsValid = true;
        }
        else if (data.RightGaze.isValid)
        {
            data.AverageGazePosition = data.RightGaze.gazePose.position.ToUnityVector();
            data.AverageGazeRotation = data.RightGaze.gazePose.orientation.ToUnityQuaternion();
            data.IsValid = true;
        }
    }
    
    /// <summary>
    /// Ham verileri loglar ve görselleştirme uygular
    /// </summary>
    private void LogAndVisualizeData(in EyeTrackingData data)
    {
        if (logRawEyeData)
        {
            LogRawEyeTrackingData(
                data.LeftGaze, 
                data.RightGaze, 
                data.LeftPupil, 
                data.RightPupil, 
                data.LeftGeometric, 
                data.RightGeometric);
        }
        
        if (applyEyeRotationToObjects)
        {
            ApplyEyeRotationsToObjects(data.LeftGaze, data.RightGaze);
        }
    }
    
    /// <summary>
    /// Real-time blink detection işlemini yapar
    /// </summary>
    private void ProcessBlinkDetection(in EyeTrackingData data)
    {
        // Debug: Geometric data kontrolü
        if (!data.HasValidGeometricData)
        {
            if (showDebugLogs && blinkDebugLogCount < 5)
            {
                blinkDebugLogCount++;
                Debug.LogWarning($"[EyeTracking] Blink detection atlandı: HasValidGeometricData=false. " +
                    $"Geometric data alınamıyor - HTC Vive Focus Vision geometric API kontrol edin.");
            }
            return;
        }
        
        float avgEyeOpenness = (data.LeftEyeOpenness + data.RightEyeOpenness) * 0.5f;
        float effectiveThreshold = Mathf.Max(blinkThreshold, MIN_BLINK_THRESHOLD);
        bool eyesClosed = avgEyeOpenness < effectiveThreshold;
        float currentTimeMs = Time.time * MILLISECONDS_PER_SECOND;
        
        // Debug: Göz açıklığı değerlerini logla (ilk birkaç kez)
        if (showDebugLogs && eyeOpennessDebugLogCount < 10)
        {
            eyeOpennessDebugLogCount++;
            Debug.Log($"[EyeTracking] Göz açıklığı: Left={data.LeftEyeOpenness:F3}, Right={data.RightEyeOpenness:F3}, " +
                $"Avg={avgEyeOpenness:F3}, Threshold={effectiveThreshold:F3}, EyesClosed={eyesClosed}");
        }
        
        if (eyesClosed && !isCurrentlyBlinking)
        {
            // Blink başlangıcı
            isCurrentlyBlinking = true;
            blinkStartTimeMs = currentTimeMs;
        }
        else if (!eyesClosed && isCurrentlyBlinking)
        {
            // Blink sonu
            isCurrentlyBlinking = false;
            float blinkDurationMs = currentTimeMs - blinkStartTimeMs;
            
            if (IsValidBlinkDuration(blinkDurationMs))
            {
                UpdateBlinkStatistics(blinkDurationMs);
            }
            
            blinkStartTimeMs = 0f;
        }
    }
    
    /// <summary>
    /// Blink süresinin geçerli aralıkta olup olmadığını kontrol eder
    /// </summary>
    private bool IsValidBlinkDuration(float durationMs)
    {
        return durationMs > MIN_BLINK_DURATION_MS && durationMs < MAX_BLINK_DURATION_MS;
    }
    
    /// <summary>
    /// Blink kaydını timestamp ile birlikte saklar (sliding window için)
    /// </summary>
    private void UpdateBlinkStatistics(float blinkDurationMs)
    {
        BlinkRecord record = new BlinkRecord
        {
            timestamp = Time.time,
            durationMs = blinkDurationMs
        };
        blinkRecords.Add(record);
        
        if (showDebugLogs)
        {
            int recentBlinkCount = GetBlinkCountInWindow();
            Debug.Log($"[EyeTracking] Blink tespit edildi! Süre: {blinkDurationMs:F2}ms, Son {timeWindow}sn'de toplam: {recentBlinkCount}");
        }
    }
    
    /// <summary>
    /// Son timeWindow içindeki blink sayısını döndürür
    /// </summary>
    private int GetBlinkCountInWindow()
    {
        float cutoffTime = Time.time - timeWindow;
        int count = 0;
        for (int i = 0; i < blinkRecords.Count; i++)
        {
            if (blinkRecords[i].timestamp >= cutoffTime)
                count++;
        }
        return count;
    }
    
    /// <summary>
    /// Göz kapalıyken (kırpma anında) gelen veriyi analizden çıkarır — anlamsız sinyaller sakkad/fiksasyon sanılmasın.
    /// </summary>
    private bool IsEyesClosedForAnalysis(in EyeTrackingData data)
    {
        if (!data.HasValidGeometricData)
            return false;
        float avgOpenness = (data.LeftEyeOpenness + data.RightEyeOpenness) * 0.5f;
        float effectiveThreshold = Mathf.Max(blinkThreshold, MIN_BLINK_THRESHOLD);
        return avgOpenness < effectiveThreshold;
    }
    
    /// <summary>
    /// Eye tracking sample'ı oluşturur ve listeye ekler. Göz kırpma anındaki veriler analizden tamamen çıkarılır.
    /// </summary>
    private void CreateAndStoreSample(in EyeTrackingData data)
    {
        if (IsEyesClosedForAnalysis(data))
            return;
        
        EyeTrackingSample sample = new EyeTrackingSample
        {
            timestamp = Time.time,
            gazePosition = data.AverageGazePosition,
            gazeRotation = data.AverageGazeRotation,
            leftEyePosition = data.LeftGaze.isValid ? data.LeftGaze.gazePose.position.ToUnityVector() : Vector3.zero,
            rightEyePosition = data.RightGaze.isValid ? data.RightGaze.gazePose.position.ToUnityVector() : Vector3.zero,
            leftEyeOpenness = data.LeftEyeOpenness,
            rightEyeOpenness = data.RightEyeOpenness,
            leftPupilDiameter = data.LeftPupilDiameter,
            rightPupilDiameter = data.RightPupilDiameter
        };
        
        samples.Add(sample);
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
    /// Zaman penceresi dışında kalan eski blink kayıtlarını temizler (sliding window)
    /// </summary>
    private void CleanupOldBlinks()
    {
        float cutoffTime = Time.time - timeWindow;
        int writeIndex = 0;
        
        for (int i = 0; i < blinkRecords.Count; i++)
        {
            if (blinkRecords[i].timestamp >= cutoffTime)
            {
                blinkRecords[writeIndex++] = blinkRecords[i];
            }
        }
        
        if (writeIndex < blinkRecords.Count)
        {
            blinkRecords.RemoveRange(writeIndex, blinkRecords.Count - writeIndex);
        }
    }
    
    /// <summary>
    /// Eye tracking verilerini tutan geçici veri yapısı
    /// </summary>
    private struct EyeTrackingData
    {
        public XrSingleEyeGazeDataHTC LeftGaze;
        public XrSingleEyeGazeDataHTC RightGaze;
        public XrSingleEyePupilDataHTC LeftPupil;
        public XrSingleEyePupilDataHTC RightPupil;
        public XrSingleEyeGeometricDataHTC LeftGeometric;
        public XrSingleEyeGeometricDataHTC RightGeometric;
        public Vector3 AverageGazePosition;
        public Quaternion AverageGazeRotation;
        public bool IsValid;
        public bool HasValidGeometricData;
        public float LeftEyeOpenness;
        public float RightEyeOpenness;
        public float LeftPupilDiameter;
        public float RightPupilDiameter;
    }
    
    /// <summary>
    /// Fixation, Saccade, Blink ve Microsaccade özelliklerini hesapla
    /// </summary>
    private void CalculateFeatures()
    {
        if (samples.Count < MIN_SAMPLES_FOR_CALCULATION)
            return;
        
        currentFeatures.Reset();
        
        CalculateFixationFeatures();
        CalculateSaccadeFeatures();
        CalculateBlinkFeatures();
        CalculateMicrosaccadeFeatures();
        
        OnFeaturesCalculated?.Invoke(currentFeatures);
    }
    
    private void CalculateFixationFeatures()
    {
        fixationDurationsBuffer.Clear();
        float currentFixationStart = samples[0].timestamp;
        int fixationCount = 0;
        
        for (int i = 1; i < samples.Count; i++)
        {
            float velocity = CalculateVelocity(samples[i-1], samples[i]);
            
            if (velocity < fixationVelocityThreshold)
            {
                continue; // Fixation devam ediyor
            }
            
            // Fixation sona erdi — 100ms'den kısa süreler gürültü sayılır, sadece gerçek odaklanmalar kaydedilir
            float duration = samples[i-1].timestamp - currentFixationStart;
            if (duration > MIN_FIXATION_DURATION_SECONDS)
            {
                fixationDurationsBuffer.Add(duration * MILLISECONDS_PER_SECOND);
                fixationCount++;
            }
            currentFixationStart = samples[i].timestamp;
        }
        
        // Son fixation'ı ekle
        if (samples.Count > 0)
        {
            float lastDuration = samples[samples.Count - 1].timestamp - currentFixationStart;
            if (lastDuration > MIN_FIXATION_DURATION_SECONDS) // 100ms altı gürültü
            {
                fixationDurationsBuffer.Add(lastDuration * MILLISECONDS_PER_SECOND);
                fixationCount++;
            }
        }
        
        if (fixationDurationsBuffer.Count > 0)
        {
            currentFeatures.Num_of_Fixations = fixationCount / timeWindow;
            currentFeatures.Mean_Fixation_Duration = CalculateMean(fixationDurationsBuffer);
            currentFeatures.SD_Fixation_Duration = CalculateStandardDeviation(fixationDurationsBuffer);
            currentFeatures.Skew_Fixation_Duration = CalculateSkewness(fixationDurationsBuffer);
            currentFeatures.Max_Fixation_Duration = CalculateMax(fixationDurationsBuffer);
            currentFeatures.First_Fixation_Duration = fixationDurationsBuffer[0];
        }
    }
    
    private void CalculateSaccadeFeatures()
    {
        saccadeDurationsBuffer.Clear();
        saccadeAmplitudesBuffer.Clear();
        saccadePeakVelocitiesBuffer.Clear();
        saccadeDirectionsBuffer.Clear();
        saccadeLengthsBuffer.Clear();
        
        float effectiveMinVelocity = Mathf.Min(saccadeMinVelocity, MIN_SACCADE_VELOCITY_DEG_PER_SEC);
        int rejectedShortDuration = 0;
        
        for (int i = 1; i < samples.Count; i++)
        {
            float velocity = CalculateVelocity(samples[i-1], samples[i]);
            float amplitude = CalculateAngularAmplitude(samples[i-1], samples[i]);
            
            if (velocity >= effectiveMinVelocity && amplitude >= MIN_SACCADE_AMPLITUDE_DEG)
            {
                float durationSec = samples[i].timestamp - samples[i-1].timestamp;
                float durationMs = durationSec * MILLISECONDS_PER_SECOND;
                
                if (durationMs < MIN_SACCADE_DURATION_MS)
                {
                    rejectedShortDuration++;
                    continue;
                }
                
                // Saccade direction RADYAN cinsinden hesaplanır (training verisiyle uyumlu)
                float direction = CalculateSaccadeDirection(samples[i-1], samples[i]);
                
                saccadeDurationsBuffer.Add(durationMs);
                saccadeAmplitudesBuffer.Add(amplitude);
                saccadePeakVelocitiesBuffer.Add(velocity);
                saccadeDirectionsBuffer.Add(direction);
                saccadeLengthsBuffer.Add(amplitude);
            }
        }
        
        if (saccadeDurationsBuffer.Count > 0 || rejectedShortDuration > 0)
        {
            if (saccadeDurationsBuffer.Count > 0)
                ApplySaccadeStatistics();
            if (enableMainSequenceValidation && Time.time - lastMainSequenceValidationTime >= MAIN_SEQUENCE_VALIDATION_INTERVAL)
            {
                ValidateMainSequence();
                ValidateShortSaccadeFilter(rejectedShortDuration, saccadeDurationsBuffer.Count);
                lastMainSequenceValidationTime = Time.time;
            }
        }
    }
    
    /// <summary>
    /// Çok kısa süreli sakkadların gürültüden ayırt edilip edilmediğini test eder; elenen ve kalan sayıyı loglar.
    /// </summary>
    private void ValidateShortSaccadeFilter(int rejectedCount, int keptCount)
    {
        if (!showDebugLogs) return;
        Debug.Log($"[EyeTracking] Sakkad gürültü filtresi: süre < {MIN_SACCADE_DURATION_MS} ms olan {rejectedCount} sakkad gürültü sayılıp elendi, {keptCount} sakkad analize alındı.");
    }
    
    /// <summary>
    /// Saccade istatistiklerini currentFeatures'a uygular
    /// </summary>
    private void ApplySaccadeStatistics()
    {
        currentFeatures.Num_of_Saccade = saccadeDurationsBuffer.Count / timeWindow;
        currentFeatures.Mean_Saccade_Duration = CalculateMean(saccadeDurationsBuffer);
        currentFeatures.SD_Saccade_Duration = CalculateStandardDeviation(saccadeDurationsBuffer);
        currentFeatures.Skew_Saccade_Duration = CalculateSkewness(saccadeDurationsBuffer);
        currentFeatures.Max_Saccade_Duration = CalculateMax(saccadeDurationsBuffer);
        currentFeatures.Mean_Saccade_Amplitude = CalculateMean(saccadeAmplitudesBuffer);
        currentFeatures.SD_Saccade_Amplitude = CalculateStandardDeviation(saccadeAmplitudesBuffer);
        currentFeatures.Skew_Saccade_Amplitude = CalculateSkewness(saccadeAmplitudesBuffer);
        currentFeatures.Max_Saccade_Amplitude = CalculateMax(saccadeAmplitudesBuffer);
        currentFeatures.Mean_Saccade_Direction = CalculateMean(saccadeDirectionsBuffer);
        currentFeatures.SD_Saccade_Direction = CalculateStandardDeviation(saccadeDirectionsBuffer);
        currentFeatures.Skew_Saccade_Direction = CalculateSkewness(saccadeDirectionsBuffer);
        currentFeatures.Max_Saccade_Direction = CalculateMax(saccadeDirectionsBuffer);
        currentFeatures.Mean_Saccade_Length = CalculateMean(saccadeLengthsBuffer);
        currentFeatures.SD_Saccade_Length = CalculateStandardDeviation(saccadeLengthsBuffer);
        currentFeatures.Skew_Saccade_Length = CalculateSkewness(saccadeLengthsBuffer);
        currentFeatures.Max_Saccade_Length = CalculateMax(saccadeLengthsBuffer);
    }
    
    /// <summary>
    /// Main Sequence sağlama: Sakkad hızı ile aldığı mesafe (genlik) arasındaki biyolojik kuralı doğrular.
    /// Uzağa bakarken göz daha hızlı hareket eder; hız-genlik korelasyonu pozitif olmalı. Rastgele veri üretilmediğini kontrol eder.
    /// </summary>
    private void ValidateMainSequence()
    {
        int n = saccadeAmplitudesBuffer.Count;
        if (n != saccadePeakVelocitiesBuffer.Count || n < 2)
            return;
        
        float correlation = CalculatePearsonCorrelation(saccadeAmplitudesBuffer, saccadePeakVelocitiesBuffer);
        bool conformsToMainSequence = correlation >= MIN_MAIN_SEQUENCE_CORRELATION;
        
        if (showDebugLogs)
        {
            string result = conformsToMainSequence
                ? "veriler biyolojik standartlarla (Main Sequence) uyumlu — gerçek insan gözü hareketi, rastgele sayı değil"
                : "Main Sequence korelasyonu düşük — yeterli sakkad verisi veya kalibrasyon kontrol edin";
            Debug.Log($"[EyeTracking] Main Sequence sağlama: r={correlation:F3}, n={n}. {result}");
        }
        
        if (logMainSequenceDataForPlotting && n > 0 && saccadeDurationsBuffer.Count == n)
        {
            logBuilder.Clear();
            logBuilder.Append("[EyeTracking] Main Sequence grafik verisi — genlik arttıkça hız artar (grafiğe dökmek için CSV aşağıda):\n");
            logBuilder.Append("amplitude_deg,peakVelocity_degPerSec,duration_ms\n");
            int rows = Mathf.Min(n, 100);
            for (int i = 0; i < rows; i++)
            {
                logBuilder.Append(saccadeAmplitudesBuffer[i].ToString("F3")).Append(",")
                    .Append(saccadePeakVelocitiesBuffer[i].ToString("F2")).Append(",")
                    .Append(saccadeDurationsBuffer[i].ToString("F2")).Append("\n");
            }
            if (n > 100) logBuilder.Append("... (+").Append(n - 100).Append(" satır daha)");
            Debug.Log(logBuilder.ToString());
        }
    }
    
    /// <summary>İki liste arasında Pearson korelasyon katsayısı (r). Main Sequence için hız-genlik ilişkisi.</summary>
    private float CalculatePearsonCorrelation(List<float> x, List<float> y)
    {
        if (x.Count != y.Count || x.Count < 2) return 0f;
        float meanX = CalculateMean(x);
        float meanY = CalculateMean(y);
        float sumXY = 0f, sumX2 = 0f, sumY2 = 0f;
        for (int i = 0; i < x.Count; i++)
        {
            float dx = x[i] - meanX;
            float dy = y[i] - meanY;
            sumXY += dx * dy;
            sumX2 += dx * dx;
            sumY2 += dy * dy;
        }
        float denom = Mathf.Sqrt(sumX2 * sumY2);
        return denom > 1e-10f ? Mathf.Clamp(sumXY / denom, -1f, 1f) : 0f;
    }
    
    /// <summary>
    /// Blink özelliklerini hesaplar (sliding window - son timeWindow içindeki blink'ler)
    /// </summary>
    private void CalculateBlinkFeatures()
    {
        // blinkRecords zaten CleanupOldBlinks() tarafından filtrelenmiş durumda
        int blinkCount = blinkRecords.Count;
        
        if (blinkCount == 0)
        {
            ResetBlinkFeatures();
            return;
        }
        
        // Blink süreleri buffer'ını doldur (istatistik hesaplamaları için)
        blinkDurationsBuffer.Clear();
        float sumDuration = 0f;
        float maxDuration = 0f;
        
        for (int i = 0; i < blinkCount; i++)
        {
            float duration = blinkRecords[i].durationMs;
            blinkDurationsBuffer.Add(duration);
            sumDuration += duration;
            if (duration > maxDuration)
                maxDuration = duration;
        }
        
        // Temel istatistikler
        currentFeatures.Num_of_Blink = blinkCount / timeWindow;
        currentFeatures.Mean_Blink_Duration = sumDuration / blinkCount;
        currentFeatures.Max_Blink_Duration = maxDuration;
        
        if (blinkCount > 1)
        {
            currentFeatures.SD_Blink_Duration = CalculateStandardDeviation(blinkDurationsBuffer);
            currentFeatures.Skew_Blink_Duration = CalculateSkewness(blinkDurationsBuffer);
        }
        else
        {
            currentFeatures.SD_Blink_Duration = 0f;
            currentFeatures.Skew_Blink_Duration = 0f;
        }
    }
    
    /// <summary>
    /// Blink özelliklerini sıfırlar
    /// </summary>
    private void ResetBlinkFeatures()
    {
        currentFeatures.Num_of_Blink = 0f;
        currentFeatures.Mean_Blink_Duration = 0f;
        currentFeatures.SD_Blink_Duration = 0f;
        currentFeatures.Skew_Blink_Duration = 0f;
        currentFeatures.Max_Blink_Duration = 0f;
    }
    
    private void CalculateMicrosaccadeFeatures()
    {
        microsaccadePeakVelocitiesBuffer.Clear();
        microsaccadeAmplitudesBuffer.Clear();
        microsaccadeDirectionsBuffer.Clear();
        microsaccadeHAmplitudesBuffer.Clear();
        microsaccadeVAmplitudesBuffer.Clear();
        
        float effectiveMinVelocity = Mathf.Min(saccadeMinVelocity, MIN_SACCADE_VELOCITY_DEG_PER_SEC);
        
        for (int i = 1; i < samples.Count; i++)
        {
            float velocity = CalculateVelocity(samples[i-1], samples[i]);
            float amplitude = CalculateAngularAmplitude(samples[i-1], samples[i]);
            
            if (IsMicrosaccade(velocity, amplitude, effectiveMinVelocity))
            {
                // Microsaccade direction DERECE cinsinden hesaplanır (training verisiyle uyumlu)
                float direction = CalculateMicrosaccadeDirection(samples[i-1], samples[i]);
                float hAmplitude = CalculateHorizontalAmplitude(samples[i-1], samples[i]);
                float vAmplitude = CalculateVerticalAmplitude(samples[i-1], samples[i]);
                
                microsaccadePeakVelocitiesBuffer.Add(velocity);
                microsaccadeAmplitudesBuffer.Add(amplitude);
                microsaccadeDirectionsBuffer.Add(direction);
                microsaccadeHAmplitudesBuffer.Add(hAmplitude);
                microsaccadeVAmplitudesBuffer.Add(vAmplitude);
            }
        }
        
        if (microsaccadePeakVelocitiesBuffer.Count > 0)
        {
            ApplyMicrosaccadeStatistics();
            if (Time.time - lastMicrosaccadeValidationTime >= MICROSACCADE_VALIDATION_INTERVAL)
            {
                RunMicrosaccadeValidations();
                lastMicrosaccadeValidationTime = Time.time;
            }
        }
    }
    
    /// <summary>
    /// Mikrosakkad ile ilgili üç sağlamayı çalıştırır: (1) Engbert-Kliegl hız eşiği, (2) Pisagor H/V, (3) fiksasyon oranı.
    /// </summary>
    private void RunMicrosaccadeValidations()
    {
        if (enableMicrosaccadeVelocityValidation)
            ValidateMicrosaccadeVelocityThresholds();
        if (enablePythagoreanValidation && microsaccadeAmplitudesBuffer.Count > 0)
            ValidatePythagoreanHV();
        if (enableMicrosaccadeFixationProportionCheck && fixationDurationsBuffer.Count > 0)
            ValidateMicrosaccadeFixationProportion();
    }
    
    /// <summary>
    /// Mikrosakkad peak velocity'lerin Engbert & Kliegl algoritmasındaki tipik eşik aralığını (örn. 30–200 deg/s) aşıp aşmadığını kontrol eder.
    /// </summary>
    private void ValidateMicrosaccadeVelocityThresholds()
    {
        if (microsaccadePeakVelocitiesBuffer.Count == 0) return;
        int inRange = 0;
        float minV = float.MaxValue, maxV = float.MinValue;
        for (int i = 0; i < microsaccadePeakVelocitiesBuffer.Count; i++)
        {
            float v = microsaccadePeakVelocitiesBuffer[i];
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
            if (v >= ENGBERT_MIN_PEAK_VELOCITY_DEG_PER_SEC && v <= ENGBERT_MAX_PEAK_VELOCITY_DEG_PER_SEC)
                inRange++;
        }
        int n = microsaccadePeakVelocitiesBuffer.Count;
        bool pass = (inRange == n) && minV >= ENGBERT_MIN_PEAK_VELOCITY_DEG_PER_SEC && maxV <= ENGBERT_MAX_PEAK_VELOCITY_DEG_PER_SEC;
        if (showDebugLogs)
            Debug.Log($"[EyeTracking] Engbert-Kliegl mikrosakkad hız: min={minV:F1}, max={maxV:F1} deg/s, eşik [{ENGBERT_MIN_PEAK_VELOCITY_DEG_PER_SEC}-{ENGBERT_MAX_PEAK_VELOCITY_DEG_PER_SEC}]. İçeride: {inRange}/{n}. {(pass ? "Eşikler uyumlu." : "Bazı değerler eşik dışında.")}");
    }
    
    /// <summary>
    /// Yatay (H) ve dikey (V) bileşenlerin kareleri toplamının karekökünün toplam genliği verip vermediğini (Pisagor) rastgele örneklerle test eder.
    /// </summary>
    private void ValidatePythagoreanHV()
    {
        int n = microsaccadeAmplitudesBuffer.Count;
        if (n != microsaccadeHAmplitudesBuffer.Count || n != microsaccadeVAmplitudesBuffer.Count || n == 0) return;
        int samplesToTest = Mathf.Min(PYTHAGOREAN_RANDOM_SAMPLES, n);
        float maxErrorDeg = 0f;
        int passed = 0;
        for (int k = 0; k < samplesToTest; k++)
        {
            int idx = UnityEngine.Random.Range(0, n);
            float totalA = microsaccadeAmplitudesBuffer[idx];
            float h = microsaccadeHAmplitudesBuffer[idx];
            float v = microsaccadeVAmplitudesBuffer[idx];
            float hvNorm = Mathf.Sqrt(h * h + v * v);
            float errorAbs = Mathf.Abs(hvNorm - totalA);
            float errorRel = totalA > 1e-6f ? errorAbs / totalA : 0f;
            if (errorAbs > maxErrorDeg) maxErrorDeg = errorAbs;
            if (errorAbs <= PYTHAGOREAN_TOLERANCE_DEG || errorRel <= PYTHAGOREAN_TOLERANCE_RELATIVE)
                passed++;
        }
        bool pass = passed == samplesToTest;
        if (showDebugLogs)
            Debug.Log($"[EyeTracking] Pisagor H/V sağlama: sqrt(H²+V²) vs toplam genlik, {samplesToTest} rastgele örnek. Geçen: {passed}/{samplesToTest}, max hata: {maxErrorDeg:F4}°. {(pass ? "Pisagor bağıntısı sağlanıyor." : "Tolerans aşan örnek var.")}");
    }
    
    /// <summary>
    /// Mikrosakkad sayısının (Num_of_Microsac) fiksasyon süreleriyle orantılı olup olmadığını inceler (uzun fiksasyon => daha fazla mikrosakkad beklentisi).
    /// </summary>
    private void ValidateMicrosaccadeFixationProportion()
    {
        if (fixationDurationsBuffer.Count == 0 || microsaccadePeakVelocitiesBuffer.Count == 0) return;
        float totalFixationMs = 0f;
        for (int i = 0; i < fixationDurationsBuffer.Count; i++)
            totalFixationMs += fixationDurationsBuffer[i];
        float totalFixationSec = totalFixationMs / MILLISECONDS_PER_SECOND;
        if (totalFixationSec <= 0f) return;
        int microsacCount = microsaccadePeakVelocitiesBuffer.Count;
        float microsacPerSecFixation = microsacCount / totalFixationSec;
        float meanFixationMs = CalculateMean(fixationDurationsBuffer);
        if (showDebugLogs)
            Debug.Log($"[EyeTracking] Mikrosakkad–fiksasyon oranı: toplam fiksasyon süresi={totalFixationSec:F2}s, fiksasyon sayısı={fixationDurationsBuffer.Count}, ortalama fiksasyon={meanFixationMs:F0}ms, mikrosakkad sayısı={microsacCount}, mikrosakkad/fiksasyon_saniyesi={microsacPerSecFixation:F2}. (Beklenti: fiksasyon süresi arttıkça mikrosakkad sayısı orantılı artar.)");
    }
    
    /// <summary>
    /// Hareketin microsaccade olup olmadığını kontrol eder
    /// </summary>
    private bool IsMicrosaccade(float velocity, float amplitude, float minVelocity)
    {
        return velocity >= minVelocity 
            && amplitude <= microsaccadeMaxAmplitude 
            && amplitude >= MIN_MICROSACCADE_AMPLITUDE_DEG;
    }
    
    /// <summary>
    /// Microsaccade istatistiklerini currentFeatures'a uygular
    /// </summary>
    private void ApplyMicrosaccadeStatistics()
    {
        currentFeatures.Num_of_Microsac = microsaccadePeakVelocitiesBuffer.Count / timeWindow;
        currentFeatures.Mean_Microsac_Peak_Vel = CalculateMean(microsaccadePeakVelocitiesBuffer);
        currentFeatures.SD_Microsac_Peak_Vel = CalculateStandardDeviation(microsaccadePeakVelocitiesBuffer);
        currentFeatures.Skew_Microsac_Peak_Vel = CalculateSkewness(microsaccadePeakVelocitiesBuffer);
        currentFeatures.Max_Microsac_Peak_Vel = CalculateMax(microsaccadePeakVelocitiesBuffer);
        currentFeatures.Mean_Microsac_Ampl = CalculateMean(microsaccadeAmplitudesBuffer);
        currentFeatures.SD_Microsac_Ampl = CalculateStandardDeviation(microsaccadeAmplitudesBuffer);
        currentFeatures.Skew_Microsac_Ampl = CalculateSkewness(microsaccadeAmplitudesBuffer);
        currentFeatures.Max_Microsac_Ampl = CalculateMax(microsaccadeAmplitudesBuffer);
        currentFeatures.Mean_Microsac_Dir = CalculateMean(microsaccadeDirectionsBuffer);
        currentFeatures.SD_Microsac_Dir = CalculateStandardDeviation(microsaccadeDirectionsBuffer);
        currentFeatures.Skew_Microsac_Dir = CalculateSkewness(microsaccadeDirectionsBuffer);
        currentFeatures.Max_Microsac_Dir = CalculateMax(microsaccadeDirectionsBuffer);
        currentFeatures.Mean_Microsac_H_Amp = CalculateMean(microsaccadeHAmplitudesBuffer);
        currentFeatures.SD_Microsac_H_Amp = CalculateStandardDeviation(microsaccadeHAmplitudesBuffer);
        currentFeatures.Skew_Microsac_H_Amp = CalculateSkewness(microsaccadeHAmplitudesBuffer);
        currentFeatures.Max_Microsac_H_Amp = CalculateMax(microsaccadeHAmplitudesBuffer);
        currentFeatures.Mean_Microsac_V_Amp = CalculateMean(microsaccadeVAmplitudesBuffer);
        currentFeatures.SD_Microsac_V_Amp = CalculateStandardDeviation(microsaccadeVAmplitudesBuffer);
        currentFeatures.Skew_Microsac_V_Amp = CalculateSkewness(microsaccadeVAmplitudesBuffer);
        currentFeatures.Max_Microsac_V_Amp = CalculateMax(microsaccadeVAmplitudesBuffer);
    }
    
    /// <summary>
    /// Göz rotasyonlarını hedef GameObject'lere uygula
    /// </summary>
    private void ApplyEyeRotationsToObjects(XrSingleEyeGazeDataHTC leftGaze, XrSingleEyeGazeDataHTC rightGaze)
    {
        ApplyEyeRotation(leftGaze, leftEyeRotationTarget);
        ApplyEyeRotation(rightGaze, rightEyeRotationTarget);
    }
    
    /// <summary>
    /// Tek bir göz rotasyonunu hedef GameObject'e uygular
    /// </summary>
    private void ApplyEyeRotation(XrSingleEyeGazeDataHTC gaze, GameObject target)
    {
        if (target == null || !gaze.isValid)
            return;
        
        Quaternion rotation = gaze.gazePose.orientation.ToUnityQuaternion();
        Vector3 euler = rotation.eulerAngles;
        
        // Yatay (Y) ve dikey (X) eksenleri ters çevir (ayna düzeltmesi)
        euler.x = -euler.x;
        euler.y = -euler.y;
        
        target.transform.rotation = Quaternion.Euler(euler);
    }
    
    /// <summary>
    /// Ham eye tracking verilerini logla (HTC'nin verebildiği tüm işlenmemiş veriler)
    /// GC optimizasyonu: StringBuilder kullanarak string allocation önlenir
    /// </summary>
    private void LogRawEyeTrackingData(
        XrSingleEyeGazeDataHTC leftGaze, 
        XrSingleEyeGazeDataHTC rightGaze,
        XrSingleEyePupilDataHTC leftPupil, 
        XrSingleEyePupilDataHTC rightPupil,
        XrSingleEyeGeometricDataHTC leftGeometric, 
        XrSingleEyeGeometricDataHTC rightGeometric)
    {
        logBuilder.Clear();
        
        logBuilder.Append("\n========== HAM EYE TRACKING VERİLERİ (Frame: ");
        logBuilder.Append(Time.frameCount);
        logBuilder.Append(", Time: ");
        logBuilder.Append(Time.time.ToString("F3"));
        logBuilder.Append("s) ==========\n");
        
        // ===== SOL GÖZ GAZE VERİLERİ =====
        logBuilder.Append("\n----- SOL GÖZ GAZE (XrSingleEyeGazeDataHTC) -----\n");
        logBuilder.Append("  isValid: ").Append(leftGaze.isValid ? "True" : "False").Append("\n");
        logBuilder.Append("  gazePose.position: (").Append(leftGaze.gazePose.position.x.ToString("F6")).Append(", ")
            .Append(leftGaze.gazePose.position.y.ToString("F6")).Append(", ")
            .Append(leftGaze.gazePose.position.z.ToString("F6")).Append(")\n");
        logBuilder.Append("  gazePose.orientation: (").Append(leftGaze.gazePose.orientation.x.ToString("F6")).Append(", ")
            .Append(leftGaze.gazePose.orientation.y.ToString("F6")).Append(", ")
            .Append(leftGaze.gazePose.orientation.z.ToString("F6")).Append(", ")
            .Append(leftGaze.gazePose.orientation.w.ToString("F6")).Append(")\n");
        
        // ===== SAĞ GÖZ GAZE VERİLERİ =====
        logBuilder.Append("\n----- SAĞ GÖZ GAZE (XrSingleEyeGazeDataHTC) -----\n");
        logBuilder.Append("  isValid: ").Append(rightGaze.isValid ? "True" : "False").Append("\n");
        logBuilder.Append("  gazePose.position: (").Append(rightGaze.gazePose.position.x.ToString("F6")).Append(", ")
            .Append(rightGaze.gazePose.position.y.ToString("F6")).Append(", ")
            .Append(rightGaze.gazePose.position.z.ToString("F6")).Append(")\n");
        logBuilder.Append("  gazePose.orientation: (").Append(rightGaze.gazePose.orientation.x.ToString("F6")).Append(", ")
            .Append(rightGaze.gazePose.orientation.y.ToString("F6")).Append(", ")
            .Append(rightGaze.gazePose.orientation.z.ToString("F6")).Append(", ")
            .Append(rightGaze.gazePose.orientation.w.ToString("F6")).Append(")\n");
        
        // ===== SOL GÖZ PUPİL VERİLERİ =====
        logBuilder.Append("\n----- SOL GÖZ PUPİL (XrSingleEyePupilDataHTC) -----\n");
        logBuilder.Append("  isDiameterValid: ").Append(leftPupil.isDiameterValid ? "True" : "False").Append("\n");
        logBuilder.Append("  pupilDiameter: ").Append(leftPupil.pupilDiameter.ToString("F6")).Append(" mm\n");
        logBuilder.Append("  isPositionValid: ").Append(leftPupil.isPositionValid ? "True" : "False").Append("\n");
        logBuilder.Append("  pupilPosition: (").Append(leftPupil.pupilPosition.x.ToString("F6")).Append(", ")
            .Append(leftPupil.pupilPosition.y.ToString("F6")).Append(")\n");
        
        // ===== SAĞ GÖZ PUPİL VERİLERİ =====
        logBuilder.Append("\n----- SAĞ GÖZ PUPİL (XrSingleEyePupilDataHTC) -----\n");
        logBuilder.Append("  isDiameterValid: ").Append(rightPupil.isDiameterValid ? "True" : "False").Append("\n");
        logBuilder.Append("  pupilDiameter: ").Append(rightPupil.pupilDiameter.ToString("F6")).Append(" mm\n");
        logBuilder.Append("  isPositionValid: ").Append(rightPupil.isPositionValid ? "True" : "False").Append("\n");
        logBuilder.Append("  pupilPosition: (").Append(rightPupil.pupilPosition.x.ToString("F6")).Append(", ")
            .Append(rightPupil.pupilPosition.y.ToString("F6")).Append(")\n");
        
        // ===== SOL GÖZ GEOMETRİK VERİLERİ =====
        logBuilder.Append("\n----- SOL GÖZ GEOMETRİK (XrSingleEyeGeometricDataHTC) -----\n");
        logBuilder.Append("  isValid: ").Append(leftGeometric.isValid ? "True" : "False").Append("\n");
        logBuilder.Append("  eyeOpenness: ").Append(leftGeometric.eyeOpenness.ToString("F6")).Append(" (0=kapalı, 1=açık)\n");
        logBuilder.Append("  eyeSqueeze: ").Append(leftGeometric.eyeSqueeze.ToString("F6")).Append("\n");
        logBuilder.Append("  eyeWide: ").Append(leftGeometric.eyeWide.ToString("F6")).Append("\n");
        
        // ===== SAĞ GÖZ GEOMETRİK VERİLERİ =====
        logBuilder.Append("\n----- SAĞ GÖZ GEOMETRİK (XrSingleEyeGeometricDataHTC) -----\n");
        logBuilder.Append("  isValid: ").Append(rightGeometric.isValid ? "True" : "False").Append("\n");
        logBuilder.Append("  eyeOpenness: ").Append(rightGeometric.eyeOpenness.ToString("F6")).Append(" (0=kapalı, 1=açık)\n");
        logBuilder.Append("  eyeSqueeze: ").Append(rightGeometric.eyeSqueeze.ToString("F6")).Append("\n");
        logBuilder.Append("  eyeWide: ").Append(rightGeometric.eyeWide.ToString("F6")).Append("\n");
        
        logBuilder.Append("\n==========================================================\n");
        
        Debug.Log(logBuilder.ToString());
    }
    
    // ========== YARDIMCI HESAPLAMA METODLARI ==========
    
    /// <summary>
    /// İki sample arasındaki açısal hızı hesaplar (derece/saniye)
    /// </summary>
    private float CalculateVelocity(EyeTrackingSample s1, EyeTrackingSample s2)
    {
        float timeDelta = s2.timestamp - s1.timestamp;
        if (timeDelta <= 0f)
            return 0f;
        
        Vector3 gazeDir1 = s1.gazeRotation * Vector3.forward;
        Vector3 gazeDir2 = s2.gazeRotation * Vector3.forward;
        float angleDegrees = Vector3.Angle(gazeDir1, gazeDir2);
        
        return angleDegrees / timeDelta;
    }
    
    /// <summary>
    /// İki sample arasındaki açısal genlik hesapla (derece cinsinden)
    /// </summary>
    private float CalculateAngularAmplitude(EyeTrackingSample s1, EyeTrackingSample s2)
    {
        Vector3 gazeDir1 = s1.gazeRotation * Vector3.forward;
        Vector3 gazeDir2 = s2.gazeRotation * Vector3.forward;
        return Vector3.Angle(gazeDir1, gazeDir2);
    }
    
    /// <summary>
    /// Yatay açısal genliği hesaplar (derece cinsinden)
    /// </summary>
    private float CalculateHorizontalAmplitude(EyeTrackingSample s1, EyeTrackingSample s2)
    {
        Vector3 gazeDir1 = s1.gazeRotation * Vector3.forward;
        Vector3 gazeDir2 = s2.gazeRotation * Vector3.forward;
        
        Vector2 horizontal1 = new Vector2(gazeDir1.x, gazeDir1.z).normalized;
        Vector2 horizontal2 = new Vector2(gazeDir2.x, gazeDir2.z).normalized;
        
        float dot = Mathf.Clamp(Vector2.Dot(horizontal1, horizontal2), -1f, 1f);
        return Mathf.Acos(dot) * Mathf.Rad2Deg;
    }
    
    /// <summary>
    /// Dikey açısal genliği hesaplar (derece cinsinden)
    /// </summary>
    private float CalculateVerticalAmplitude(EyeTrackingSample s1, EyeTrackingSample s2)
    {
        Vector3 gazeDir1 = s1.gazeRotation * Vector3.forward;
        Vector3 gazeDir2 = s2.gazeRotation * Vector3.forward;
        
        float pitch1 = Mathf.Asin(gazeDir1.y) * Mathf.Rad2Deg;
        float pitch2 = Mathf.Asin(gazeDir2.y) * Mathf.Rad2Deg;
        
        return Mathf.Abs(pitch2 - pitch1);
    }
    
    /// <summary>
    /// Saccade yönünü hesaplar (RADYAN cinsinden, -π ile +π arası)
    /// NOT: Training verisi radyan kullandığı için radyan döndürülür.
    /// Bu drift sorununu çözer: Train Mean=-0.13, Max=3.11 (radyan)
    /// </summary>
    private float CalculateSaccadeDirection(EyeTrackingSample s1, EyeTrackingSample s2)
    {
        Vector3 gazeDir1 = s1.gazeRotation * Vector3.forward;
        Vector3 gazeDir2 = s2.gazeRotation * Vector3.forward;
        Vector3 movement = gazeDir2 - gazeDir1;
        
        // Training verisi radyan kullanıyor (max ~π), bu yüzden radyan döndür
        return Mathf.Atan2(movement.y, movement.x);
    }
    
    /// <summary>
    /// Microsaccade yönünü hesaplar (DERECE cinsinden, -180 ile +180 arası)
    /// NOT: Training verisi microsaccade için derece kullandığı için derece döndürülür.
    /// Train Max=179.69 (derece)
    /// </summary>
    private float CalculateMicrosaccadeDirection(EyeTrackingSample s1, EyeTrackingSample s2)
    {
        Vector3 gazeDir1 = s1.gazeRotation * Vector3.forward;
        Vector3 gazeDir2 = s2.gazeRotation * Vector3.forward;
        Vector3 movement = gazeDir2 - gazeDir1;
        
        // Training verisi microsaccade için derece kullanıyor, bu yüzden derece döndür
        return Mathf.Atan2(movement.y, movement.x) * Mathf.Rad2Deg;
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
    
    private float CalculateSkewness(List<float> values)
    {
        if (values.Count < MIN_SAMPLES_FOR_SKEWNESS)
            return 0f;
        
        float mean = CalculateMean(values);
        float stdDev = CalculateStandardDeviation(values);
        if (stdDev == 0f)
            return 0f;
        
        float sum = 0f;
        for (int i = 0; i < values.Count; i++)
        {
            float normalized = (values[i] - mean) / stdDev;
            sum += normalized * normalized * normalized;
        }
        
        return sum / values.Count;
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
    public EyeTrackingFeatures GetFeatures()
    {
        return currentFeatures;
    }
    
    /// <summary>
    /// Veri toplamayı başlat/durdur
    /// </summary>
    public void StartCollecting()
    {
        startCollecting = true;
        samples.Clear();
        ResetBlinkTracking();
    }
    
    public void StopCollecting()
    {
        startCollecting = false;
    }
    
    /// <summary>
    /// Blink tracking değişkenlerini sıfırla (veri toplama yeniden başladığında)
    /// </summary>
    private void ResetBlinkTracking()
    {
        isCurrentlyBlinking = false;
        blinkStartTimeMs = 0f;
        blinkRecords.Clear();
        blinkDurationsBuffer.Clear();
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
}

// Veri yapıları
// GC optimizasyonu: class yerine struct kullanarak heap allocation önlenir
[Serializable]
public struct EyeTrackingSample
{
    public float timestamp;
    public Vector3 gazePosition;
    public Quaternion gazeRotation;
    public Vector3 leftEyePosition;
    public Vector3 rightEyePosition;
    public float leftEyeOpenness;
    public float rightEyeOpenness;
    public float leftPupilDiameter;
    public float rightPupilDiameter;
}

[Serializable]
public class EyeTrackingFeatures
{
    // Fixation özellikleri
    public float Num_of_Fixations = 0f;
    public float Mean_Fixation_Duration = 0f;
    public float SD_Fixation_Duration = 0f;
    public float Skew_Fixation_Duration = 0f;
    public float Max_Fixation_Duration = 0f;
    public float First_Fixation_Duration = 0f;
    
    // Saccade özellikleri
    public float Num_of_Saccade = 0f;
    public float Mean_Saccade_Duration = 0f;
    public float SD_Saccade_Duration = 0f;
    public float Skew_Saccade_Duration = 0f;
    public float Max_Saccade_Duration = 0f;
    public float Mean_Saccade_Amplitude = 0f;
    public float SD_Saccade_Amplitude = 0f;
    public float Skew_Saccade_Amplitude = 0f;
    public float Max_Saccade_Amplitude = 0f;
    public float Mean_Saccade_Direction = 0f;
    public float SD_Saccade_Direction = 0f;
    public float Skew_Saccade_Direction = 0f;
    public float Max_Saccade_Direction = 0f;
    public float Mean_Saccade_Length = 0f;
    public float SD_Saccade_Length = 0f;
    public float Skew_Saccade_Length = 0f;
    public float Max_Saccade_Length = 0f;
    
    // Blink özellikleri
    public float Num_of_Blink = 0f;
    public float Mean_Blink_Duration = 0f;
    public float SD_Blink_Duration = 0f;
    public float Skew_Blink_Duration = 0f;
    public float Max_Blink_Duration = 0f;
    
    // Microsaccade özellikleri
    public float Num_of_Microsac = 0f;
    public float Mean_Microsac_Peak_Vel = 0f;
    public float SD_Microsac_Peak_Vel = 0f;
    public float Skew_Microsac_Peak_Vel = 0f;
    public float Max_Microsac_Peak_Vel = 0f;
    public float Mean_Microsac_Ampl = 0f;
    public float SD_Microsac_Ampl = 0f;
    public float Skew_Microsac_Ampl = 0f;
    public float Max_Microsac_Ampl = 0f;
    public float Mean_Microsac_Dir = 0f;
    public float SD_Microsac_Dir = 0f;
    public float Skew_Microsac_Dir = 0f;
    public float Max_Microsac_Dir = 0f;
    public float Mean_Microsac_H_Amp = 0f;
    public float SD_Microsac_H_Amp = 0f;
    public float Skew_Microsac_H_Amp = 0f;
    public float Max_Microsac_H_Amp = 0f;
    public float Mean_Microsac_V_Amp = 0f;
    public float SD_Microsac_V_Amp = 0f;
    public float Skew_Microsac_V_Amp = 0f;
    public float Max_Microsac_V_Amp = 0f;
    
    /// <summary>
    /// Tüm özellikleri sıfırla (GC optimizasyonu: yeni obje oluşturmak yerine)
    /// </summary>
    public void Reset()
    {
        // Fixation özellikleri
        Num_of_Fixations = 0f;
        Mean_Fixation_Duration = 0f;
        SD_Fixation_Duration = 0f;
        Skew_Fixation_Duration = 0f;
        Max_Fixation_Duration = 0f;
        First_Fixation_Duration = 0f;
        
        // Saccade özellikleri
        Num_of_Saccade = 0f;
        Mean_Saccade_Duration = 0f;
        SD_Saccade_Duration = 0f;
        Skew_Saccade_Duration = 0f;
        Max_Saccade_Duration = 0f;
        Mean_Saccade_Amplitude = 0f;
        SD_Saccade_Amplitude = 0f;
        Skew_Saccade_Amplitude = 0f;
        Max_Saccade_Amplitude = 0f;
        Mean_Saccade_Direction = 0f;
        SD_Saccade_Direction = 0f;
        Skew_Saccade_Direction = 0f;
        Max_Saccade_Direction = 0f;
        Mean_Saccade_Length = 0f;
        SD_Saccade_Length = 0f;
        Skew_Saccade_Length = 0f;
        Max_Saccade_Length = 0f;
        
        // Blink özellikleri
        Num_of_Blink = 0f;
        Mean_Blink_Duration = 0f;
        SD_Blink_Duration = 0f;
        Skew_Blink_Duration = 0f;
        Max_Blink_Duration = 0f;
        
        // Microsaccade özellikleri
        Num_of_Microsac = 0f;
        Mean_Microsac_Peak_Vel = 0f;
        SD_Microsac_Peak_Vel = 0f;
        Skew_Microsac_Peak_Vel = 0f;
        Max_Microsac_Peak_Vel = 0f;
        Mean_Microsac_Ampl = 0f;
        SD_Microsac_Ampl = 0f;
        Skew_Microsac_Ampl = 0f;
        Max_Microsac_Ampl = 0f;
        Mean_Microsac_Dir = 0f;
        SD_Microsac_Dir = 0f;
        Skew_Microsac_Dir = 0f;
        Max_Microsac_Dir = 0f;
        Mean_Microsac_H_Amp = 0f;
        SD_Microsac_H_Amp = 0f;
        Skew_Microsac_H_Amp = 0f;
        Max_Microsac_H_Amp = 0f;
        Mean_Microsac_V_Amp = 0f;
        SD_Microsac_V_Amp = 0f;
        Skew_Microsac_V_Amp = 0f;
        Max_Microsac_V_Amp = 0f;
    }
    
    /// <summary>
    /// Dictionary formatına çevir (Unity_Example.cs için)
    /// v3 model: MI (Mutual Information) bazlı özellik seçimi sonrası sadece 14 eye tracking özelliği kullanılır.
    /// Log dönüşüm sunucu tarafında (websocket_server.py) otomatik uygulanır.
    /// </summary>
    public Dictionary<string, float> ToDictionary()
    {
        return new Dictionary<string, float>
        {
            // ========== FIXATION ÖZELLİKLERİ (2 özellik) ==========
            { "Num_of_Fixations", Num_of_Fixations },
            { "Skew_Fixation_Duration", Skew_Fixation_Duration },

            // ========== SACCADE ÖZELLİKLERİ (5 özellik) ==========
            { "Mean_Saccade_Duration", Mean_Saccade_Duration },
            // Saccade Direction: RADYAN cinsinden (training verisiyle uyumlu)
            { "Mean_Saccade_Direction", Mean_Saccade_Direction },
            { "SD_Saccade_Direction", SD_Saccade_Direction },
            { "Skew_Saccade_Direction", Skew_Saccade_Direction },
            { "Max_Saccade_Direction", Max_Saccade_Direction },

            // ========== BLINK ÖZELLİKLERİ (2 özellik) ==========
            { "Num_of_Blink", Num_of_Blink },
            { "Mean_Blink_Duration", Mean_Blink_Duration },

            // ========== MICROSACCADE ÖZELLİKLERİ (5 özellik) ==========
            // Direction: DERECE cinsinden (training verisiyle uyumlu)
            { "Max_Microsac_Dir", Max_Microsac_Dir },
            // Horizontal Amplitude
            { "Mean_Microsac_H_Amp", Mean_Microsac_H_Amp },
            // Vertical Amplitude
            { "Skew_Microsac_V_Amp", Skew_Microsac_V_Amp },
            { "SD_Microsac_V_Amp", SD_Microsac_V_Amp },
            { "Max_Microsac_V_Amp", Max_Microsac_V_Amp }
        };
    }

    /// <summary>
    /// Tüm özellikleri Dictionary formatında döndürür (loglama, debug veya gelecek model eğitimi için)
    /// </summary>
    public Dictionary<string, float> ToFullDictionary()
    {
        return new Dictionary<string, float>
        {
            { "Num_of_Fixations", Num_of_Fixations },
            { "Mean_Fixation_Duration", Mean_Fixation_Duration },
            { "SD_Fixation_Duration", SD_Fixation_Duration },
            { "Skew_Fixation_Duration", Skew_Fixation_Duration },
            { "Max_Fixation_Duration", Max_Fixation_Duration },
            { "First_Fixation_Duration", (int)First_Fixation_Duration },
            { "Num_of_Saccade", Num_of_Saccade },
            { "Mean_Saccade_Duration", Mean_Saccade_Duration },
            { "SD_Saccade_Duration", SD_Saccade_Duration },
            { "Skew_Saccade_Duration", Skew_Saccade_Duration },
            { "Max_Saccade_Duration", Max_Saccade_Duration },
            { "Mean_Saccade_Amplitude", Mean_Saccade_Amplitude },
            { "SD_Saccade_Amplitude", SD_Saccade_Amplitude },
            { "Skew_Saccade_Amplitude", Skew_Saccade_Amplitude },
            { "Max_Saccade_Amplitude", Max_Saccade_Amplitude },
            { "Mean_Saccade_Direction", Mean_Saccade_Direction },
            { "SD_Saccade_Direction", SD_Saccade_Direction },
            { "Skew_Saccade_Direction", Skew_Saccade_Direction },
            { "Max_Saccade_Direction", Max_Saccade_Direction },
            { "Mean_Saccade_Length", Mean_Saccade_Length },
            { "SD_Saccade_Length", SD_Saccade_Length },
            { "Skew_Saccade_Length", Skew_Saccade_Length },
            { "Max_Saccade_Length", Max_Saccade_Length },
            { "Num_of_Blink", Num_of_Blink },
            { "Mean_Blink_Duration", Mean_Blink_Duration },
            { "SD_Blink_Duration", SD_Blink_Duration },
            { "Skew_Blink_Duration", Skew_Blink_Duration },
            { "Max_Blink_Duration", Max_Blink_Duration },
            { "Num_of_Microsac", Num_of_Microsac },
            { "Mean_Microsac_Peak_Vel", Mean_Microsac_Peak_Vel },
            { "SD_Microsac_Peak_Vel", SD_Microsac_Peak_Vel },
            { "Skew_Microsac_Peak_Vel", Skew_Microsac_Peak_Vel },
            { "Max_Microsac_Peak_Vel", Max_Microsac_Peak_Vel },
            { "Mean_Microsac_Ampl", Mean_Microsac_Ampl },
            { "SD_Microsac_Ampl", SD_Microsac_Ampl },
            { "Skew_Microsac_Ampl", Skew_Microsac_Ampl },
            { "Max_Microsac_Ampl", Max_Microsac_Ampl },
            { "Mean_Microsac_Dir", Mean_Microsac_Dir },
            { "SD_Microsac_Dir", SD_Microsac_Dir },
            { "Skew_Microsac_Dir", Skew_Microsac_Dir },
            { "Max_Microsac_Dir", Max_Microsac_Dir },
            { "Mean_Microsac_H_Amp", Mean_Microsac_H_Amp },
            { "SD_Microsac_H_Amp", SD_Microsac_H_Amp },
            { "Skew_Microsac_H_Amp", Skew_Microsac_H_Amp },
            { "Max_Microsac_H_Amp", Max_Microsac_H_Amp },
            { "Mean_Microsac_V_Amp", Mean_Microsac_V_Amp },
            { "SD_Microsac_V_Amp", SD_Microsac_V_Amp },
            { "Skew_Microsac_V_Amp", Skew_Microsac_V_Amp },
            { "Max_Microsac_V_Amp", Max_Microsac_V_Amp }
        };
    }
}

