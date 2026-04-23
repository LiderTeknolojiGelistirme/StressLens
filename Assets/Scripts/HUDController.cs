using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.PostProcessing;
using TMPro;

/// <summary>
/// Stress HUD kontrolü: Vignette rengini (stresli=kırmızı, normal=mavi) ayarlar,
/// stress yüzdesi ve durum metnini canvas TMP alanlarında gösterir.
/// Vignette renk geçişi smooth yapılır.
/// </summary>
public class HUDController : MonoBehaviour
{
    [Header("StressPredictor")]
    [Tooltip("Tahminleri alacağımız StressPredictor")]
    public StressPredictor stressPredictor;

    [Header("Post Process - Vignette")]
    [Tooltip("Vignette efektinin bulunduğu Post Process Volume (genelde kameradaki global volume)")]
    public PostProcessVolume postProcessVolume;

    [Header("Vignette Renkleri")]
    [Tooltip("Normal (sakin) durumda vignette rengi")]
    public Color normalColor = new Color(0.2f, 0.3f, 0.8f, 1f); // Mavi

    [Tooltip("Stresli durumda vignette rengi")]
    public Color stressedColor = new Color(0.9f, 0.2f, 0.2f, 1f); // Kırmızı

    [Tooltip("Renk geçişinin ne kadar hızlı olacağı (yüksek = daha hızlı)")]
    [Range(1f, 15f)]
    public float colorSmoothSpeed = 6f;

    [Header("Canvas - Screen Overlay Metinleri")]
    [Tooltip("Metinlerin arka planı (Panel üzerindeki Image). Strese göre mavi/kırmızı olur.")]
    public Image backgroundImage;

    [Tooltip("Arka plan renginin şeffaflığı (0=şeffaf, 1=opak)")]
    [Range(0f, 1f)]
    public float backgroundAlpha = 0.7f;

    [Tooltip("Stress yüzdesini gösterecek TMP (örn: %45)")]
    public TextMeshProUGUI stressPercentageText;

    [Tooltip("Stress durumunu gösterecek TMP (Stress seviyesi normal / Stress seviyesi yüksek)")]
    public TextMeshProUGUI stressStateText;

    [Header("Metin İçerikleri (Opsiyonel)")]
    [Tooltip("Normal durumda gösterilecek metin")]
    public string normalStateLabel = "Stress seviyesi normal";

    [Tooltip("Stresli durumda gösterilecek metin")]
    public string stressedStateLabel = "Stress seviyesi yüksek";

    // Güncel stress olasılığı (0–1), son gelen tahmine göre
    private float _currentStressProbability;
    // Vignette rengi için smooth hedef ve mevcut değer
    private Color _targetVignetteColor;
    private Color _currentVignetteColor;
    private Vignette _vignette;
    private bool _vignetteValid;

    void Start()
    {
        if (stressPredictor == null)
            stressPredictor = FindObjectOfType<StressPredictor>();

        if (stressPredictor == null)
        {
            Debug.LogError("[HUDController] StressPredictor bulunamadı!");
            enabled = false;
            return;
        }

        stressPredictor.OnPredictionReceived += OnPredictionReceived;

        // Vignette referansı
        if (postProcessVolume == null)
        {
            var layer = FindObjectOfType<PostProcessLayer>();
            if (layer != null)
            {
                // Global volume'ü bul (priority ile veya ilk aktif volume)
                var volumes = FindObjectsByType<PostProcessVolume>(FindObjectsSortMode.None);
                foreach (var v in volumes)
                {
                    if (v.profile != null && v.profile.HasSettings<Vignette>())
                    {
                        postProcessVolume = v;
                        break;
                    }
                }
            }
        }

        if (postProcessVolume != null && postProcessVolume.profile != null)
        {
            _vignette = postProcessVolume.profile.GetSetting<Vignette>();
            _vignetteValid = _vignette != null;
            if (_vignetteValid)
                _currentVignetteColor = _vignette.color.value;
            else
                Debug.LogWarning("[HUDController] Seçilen volume'de Vignette efekti yok.");
        }
        else
        {
            Debug.LogWarning("[HUDController] PostProcessVolume atanmadı ve uygun volume bulunamadı. Inspector'dan atayın.");
        }

        _targetVignetteColor = normalColor;
        if (_vignetteValid)
            _currentVignetteColor = normalColor;
    }

    void OnDestroy()
    {
        if (stressPredictor != null)
            stressPredictor.OnPredictionReceived -= OnPredictionReceived;
    }

    void Update()
    {
        // Hedef rengi stress olasılığına göre mavi–kırmızı arasında belirle
        _targetVignetteColor = Color.Lerp(normalColor, stressedColor, _currentStressProbability);

        // Smooth renk geçişi
        _currentVignetteColor = Color.Lerp(_currentVignetteColor, _targetVignetteColor, Time.deltaTime * colorSmoothSpeed);

        if (_vignetteValid)
            _vignette.color.Override(_currentVignetteColor);

        // Arka plan rengini aynı geçişle güncelle (mavi/kırmızı)
        if (backgroundImage != null)
        {
            Color bgColor = _currentVignetteColor;
            bgColor.a = backgroundAlpha;
            backgroundImage.color = bgColor;
        }
    }

    private void OnPredictionReceived(StressResult result)
    {
        _currentStressProbability = Mathf.Clamp01(result.stres_olasilik);

        if (stressPercentageText != null)
            stressPercentageText.text = $"{_currentStressProbability * 100f:F0}%";

        if (stressStateText != null)
        {
            stressStateText.text = result.stres == 1 ? stressedStateLabel : normalStateLabel;
        }
    }
}
