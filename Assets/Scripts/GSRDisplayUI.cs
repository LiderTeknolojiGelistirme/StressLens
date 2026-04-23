using UnityEngine;
using TMPro;

/// <summary>
/// GSR verisini Unity Canvas'te TMP_Text üzerinde gösteren script
/// </summary>
public class GSRDisplayUI : MonoBehaviour
{
    [Header("GSR Data Collector Referansı")]
    [Tooltip("GSR verilerini toplayan GSRDataCollector scripti")]
    public GSRDataCollector gsrDataCollector;
    
    [Header("UI Referansları")]
    [Tooltip("GSR değerini gösterecek TMP_Text bileşeni")]
    public TextMeshProUGUI gsrValueText;
    
    [Header("Görüntüleme Ayarları")]
    [Tooltip("Gösterilecek ondalık basamak sayısı")]
    [Range(0, 6)]
    public int decimalPlaces = 3;
    
    [Tooltip("Güncelleme frekansı (Hz) - 0 ise her veri geldiğinde güncellenir")]
    [Range(0, 60)]
    public float updateFrequency = 0f;
    
    [Tooltip("GSR değerinden önce gösterilecek metin")]
    public string prefixText = "GSR: ";
    
    [Tooltip("GSR değerinden sonra gösterilecek metin (birim vb.)")]
    public string suffixText = " µS";
    
    [Header("Renk Ayarları")]
    [Tooltip("Normal durum rengi")]
    public Color normalColor = Color.white;
    
    [Tooltip("Yüksek değer rengi (eşik değerinden yüksekse)")]
    public Color highValueColor = Color.red;
    
    [Tooltip("Yüksek değer eşiği (µS)")]
    public float highValueThreshold = 15f;
    
    private float lastUpdateTime = 0f;
    private float updateInterval = 0f;
    
    void Start()
    {
        // GSRDataCollector referansı yoksa otomatik bul
        if (gsrDataCollector == null)
        {
            gsrDataCollector = FindObjectOfType<GSRDataCollector>();
        }
        
        // TMP_Text referansı yoksa otomatik bul
        if (gsrValueText == null)
        {
            gsrValueText = GetComponent<TextMeshProUGUI>();
            if (gsrValueText == null)
            {
                Debug.LogError("[GSRDisplayUI] TMP_Text bileşeni bulunamadı! Lütfen scripti TMP_Text içeren bir GameObject'e ekleyin veya gsrValueText referansını manuel olarak atayın.");
                enabled = false;
                return;
            }
        }
        
        // GSRDataCollector referansı hala yoksa hata ver
        if (gsrDataCollector == null)
        {
            Debug.LogError("[GSRDisplayUI] GSRDataCollector bulunamadı! Lütfen gsrDataCollector referansını manuel olarak atayın.");
            enabled = false;
            return;
        }
        
        // Event'e abone ol
        gsrDataCollector.OnGSRValueUpdated += OnGSRValueReceived;
        
        // Güncelleme aralığını hesapla
        if (updateFrequency > 0f)
        {
            updateInterval = 1f / updateFrequency;
        }
        
        // İlk değeri göster
        UpdateDisplay(gsrDataCollector.GetLastGSRValue());
    }
    
    void OnDestroy()
    {
        // Event aboneliğini kaldır
        if (gsrDataCollector != null)
        {
            gsrDataCollector.OnGSRValueUpdated -= OnGSRValueReceived;
        }
    }
    
    void Update()
    {
        // Eğer updateFrequency > 0 ise, belirli aralıklarla güncelle
        if (updateFrequency > 0f && Time.time - lastUpdateTime >= updateInterval)
        {
            if (gsrDataCollector != null)
            {
                UpdateDisplay(gsrDataCollector.GetLastGSRValue());
            }
            lastUpdateTime = Time.time;
        }
    }
    
    /// <summary>
    /// GSR değeri geldiğinde çağrılır
    /// </summary>
    private void OnGSRValueReceived(float gsrValue)
    {
        // Eğer updateFrequency 0 ise, her veri geldiğinde güncelle
        if (updateFrequency <= 0f)
        {
            UpdateDisplay(gsrValue);
        }
    }
    
    /// <summary>
    /// TMP_Text'i günceller
    /// </summary>
    private void UpdateDisplay(float gsrValue)
    {
        if (gsrValueText == null)
            return;
        
        // Değeri formatla
        string formatString = "F" + decimalPlaces;
        string formattedValue = gsrValue.ToString(formatString);
        
        // Metni oluştur
        string displayText = prefixText + formattedValue + suffixText;
        gsrValueText.text = displayText;
        
        // Rengi ayarla
        if (gsrValue >= highValueThreshold)
        {
            gsrValueText.color = highValueColor;
        }
        else
        {
            gsrValueText.color = normalColor;
        }
    }
}
