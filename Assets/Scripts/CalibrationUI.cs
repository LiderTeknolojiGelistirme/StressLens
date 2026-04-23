using UnityEngine;
using TMPro;

/// <summary>
/// Kişiye özel kalibrasyon durumunu gösteren UI scripti.
/// Session başında sakin hal baseline'ı ölçülür ve dinamik threshold hesaplanır.
/// </summary>
public class CalibrationUI : MonoBehaviour
{
    [Header("StressPredictor Referansı")]
    [Tooltip("Kalibrasyon durumunu takip edeceğimiz StressPredictor")]
    public StressPredictor stressPredictor;
    
    [Header("UI Referansları")]
    [Tooltip("Kalibrasyon durumunu gösteren TMP_Text")]
    public TextMeshProUGUI calibrationStatusText;
    
    [Tooltip("Kalibrasyon ilerleme çubuğu (opsiyonel)")]
    public UnityEngine.UI.Slider progressSlider;
    
    [Tooltip("Stres tahmin sonucunu gösteren TMP_Text (opsiyonel)")]
    public TextMeshProUGUI predictionResultText;
    
    [Header("Görüntüleme Ayarları")]
    [Tooltip("Kalibrasyon sırasında gösterilecek prefix")]
    public string calibratingPrefix = "Kalibrasyon: ";
    
    [Tooltip("Kalibrasyon tamamlandığında gösterilecek prefix")]
    public string calibratedPrefix = "Hazır | Threshold: ";
    
    [Header("Renk Ayarları")]
    [Tooltip("Kalibrasyon sırasında metin rengi")]
    public Color calibratingColor = Color.yellow;
    
    [Tooltip("Kalibrasyon tamamlandığında metin rengi")]
    public Color calibratedColor = Color.green;
    
    [Tooltip("Stresli durumda metin rengi")]
    public Color stressedColor = Color.red;
    
    [Tooltip("Normal durumda metin rengi")]
    public Color normalColor = Color.white;
    
    // Son kalibrasyon durumu
    private CalibrationInfo lastCalibrationInfo;
    private bool isCalibrated = false;
    
    void Start()
    {
        // StressPredictor referansı yoksa otomatik bul
        if (stressPredictor == null)
        {
            stressPredictor = FindObjectOfType<StressPredictor>();
        }
        
        if (stressPredictor == null)
        {
            Debug.LogError("[CalibrationUI] StressPredictor bulunamadı!");
            enabled = false;
            return;
        }
        
        // Event'lere abone ol
        stressPredictor.OnCalibrationProgress += OnCalibrationProgress;
        stressPredictor.OnCalibrationComplete += OnCalibrationComplete;
        stressPredictor.OnPredictionReceived += OnPredictionReceived;
        
        // İlk durumu göster
        UpdateCalibrationDisplay(null);
    }
    
    void OnDestroy()
    {
        if (stressPredictor != null)
        {
            stressPredictor.OnCalibrationProgress -= OnCalibrationProgress;
            stressPredictor.OnCalibrationComplete -= OnCalibrationComplete;
            stressPredictor.OnPredictionReceived -= OnPredictionReceived;
        }
    }
    
    /// <summary>
    /// Kalibrasyon ilerlediğinde çağrılır
    /// </summary>
    private void OnCalibrationProgress(CalibrationInfo info)
    {
        lastCalibrationInfo = info;
        UpdateCalibrationDisplay(info);
    }
    
    /// <summary>
    /// Kalibrasyon tamamlandığında çağrılır
    /// </summary>
    private void OnCalibrationComplete(CalibrationInfo info)
    {
        isCalibrated = true;
        lastCalibrationInfo = info;
        UpdateCalibrationDisplay(info);
        
        Debug.Log($"[CalibrationUI] Kalibrasyon tamamlandı! Baseline: {info.baseline:P1}, Threshold: {info.threshold:P1}");
    }
    
    /// <summary>
    /// Tahmin sonucu geldiğinde çağrılır
    /// </summary>
    private void OnPredictionReceived(StressResult result)
    {
        UpdatePredictionDisplay(result);
    }
    
    /// <summary>
    /// Kalibrasyon durumu UI'ını günceller
    /// </summary>
    private void UpdateCalibrationDisplay(CalibrationInfo info)
    {
        if (calibrationStatusText == null) return;
        
        if (info == null)
        {
            calibrationStatusText.text = "Bağlantı bekleniyor...";
            calibrationStatusText.color = calibratingColor;
            
            if (progressSlider != null)
            {
                progressSlider.value = 0;
            }
            return;
        }
        
        if (info.is_calibrated)
        {
            // Kalibrasyon tamamlandı
            calibrationStatusText.text = $"{calibratedPrefix}{info.threshold:P0}";
            calibrationStatusText.color = calibratedColor;
            
            if (progressSlider != null)
            {
                progressSlider.value = 1f;
            }
        }
        else
        {
            // Kalibrasyon devam ediyor
            float progress = (float)info.calibration_progress / info.calibration_total;
            calibrationStatusText.text = $"{calibratingPrefix}{info.calibration_progress}/{info.calibration_total}";
            calibrationStatusText.color = calibratingColor;
            
            if (progressSlider != null)
            {
                progressSlider.value = progress;
            }
        }
    }
    
    /// <summary>
    /// Tahmin sonucu UI'ını günceller
    /// </summary>
    private void UpdatePredictionDisplay(StressResult result)
    {
        if (predictionResultText == null) return;
        
        string statusEmoji = result.stres == 1 ? "😰" : "😊";
        string statusText = result.stres == 1 ? "Stresli" : "Normal";
        
        predictionResultText.text = $"{statusEmoji} {statusText} ({result.stres_olasilik:P0})";
        predictionResultText.color = result.stres == 1 ? stressedColor : normalColor;
    }
    
    /// <summary>
    /// Kalibrasyon tamamlandı mı?
    /// </summary>
    public bool IsCalibrated()
    {
        return isCalibrated;
    }
    
    /// <summary>
    /// Son kalibrasyon bilgisini döndürür
    /// </summary>
    public CalibrationInfo GetCalibrationInfo()
    {
        return lastCalibrationInfo;
    }
}
