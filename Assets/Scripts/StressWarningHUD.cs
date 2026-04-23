using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// VR için konsolide stres uyarı HUD'u.
/// Üç işlevi tek canvas üzerinde toplar:
///   1) Model stresli değer döndüğünde sarı vignette + "Biraz dinlenmelisin" uyarısı (merkez).
///   2) Kalibrasyon ilerlemesi ve bağlantı durumu (alt panel, kalibrasyon bitince otomatik gizlenir).
///   3) İsteğe bağlı canlı GSR değeri ve tahmin metni (alt panel, default kapalı — VR'da gözü yormamak için).
///
/// Tasarım kararları (VR ergonomisi):
///   - Stres uyarısı dışında her şey alt bölgede; merkez görüşü engellemez.
///   - Saydam arka plan, küçük yazı — periferide kalır.
///   - Kalibrasyon bitince info paneli kaybolur; gereksiz bilgi göze girmez.
///   - Head-locked: canvas her frame kameranın önünde konumlanır.
/// </summary>
public class StressWarningHUD : MonoBehaviour
{
    [Header("Test Modu")]
    [Tooltip("Aktifken StressPredictor yerine rastgele değerler üretir")]
    public bool testMode = false;

    [Tooltip("Test modunda stres/normal geçiş aralığı (saniye)")]
    [Range(2f, 15f)]
    public float testInterval = 5f;

    [Header("Referanslar")]
    [Tooltip("Tahminleri alacağımız StressPredictor")]
    public StressPredictor stressPredictor;

    [Tooltip("GSR verisini sağlayan collector (showGsrValue açıksa gerekli)")]
    public GSRDataCollector gsrDataCollector;

    [Tooltip("HUD'un takip edeceği kamera. Boş bırakılırsa MainCamera tag'i ile bulunur.")]
    public Camera targetCamera;

    [Header("Uyarı Metni")]
    [Tooltip("Stres durumunda gösterilecek uyarı mesajı")]
    public string warningMessage = "Biraz dinlenmelisin";

    [Tooltip("Metin rengi")]
    public Color textColor = Color.white;

    [Tooltip("Metin font boyutu")]
    public float fontSize = 36f;

    [Header("Vignette Ayarları")]
    [Tooltip("Vignette kenar rengi (saydam sarı)")]
    public Color vignetteColor = new Color(1f, 0.85f, 0f, 0.45f);

    [Tooltip("Vignette iç yarıçapı (0-1). Düşük = daha geniş kaplama")]
    [Range(0.1f, 0.8f)]
    public float vignetteInnerRadius = 0.45f;

    [Tooltip("Vignette dış yarıçapı (0-1).")]
    [Range(0.5f, 1.2f)]
    public float vignetteOuterRadius = 1.0f;

    [Header("Geçiş Ayarları")]
    [Tooltip("Fade-in/out hızı")]
    [Range(0.5f, 10f)]
    public float fadeSpeed = 3f;

    [Tooltip("Kameradan canvas uzaklığı (metre)")]
    [Range(0.5f, 5f)]
    public float canvasDistance = 2f;

    [Header("Alt Bilgi Paneli (Kalibrasyon + GSR)")]
    [Tooltip("Kalibrasyon ilerlemesini göster. Kullanıcı süreçte olduğunu bilmeli.")]
    public bool showCalibrationProgress = true;

    [Tooltip("Kalibrasyon tamamlandıktan sonra bilgi panelini gizlemeden önce bekleme süresi (sn)")]
    [Range(0f, 10f)]
    public float calibrationHideDelay = 3f;

    [Tooltip("Canlı GSR değerini alt panelde göster (VR'da önerilmez — gözü yorar)")]
    public bool showGsrValue = false;

    [Tooltip("Stres oranını sürekli göster (örn: 'Normal %23' / 'Stresli %73').")]
    public bool showPredictionText = true;

    [Tooltip("Alt panel saydamlığı (0=şeffaf, 1=opak). VR'da 0.5-0.7 önerilir.")]
    [Range(0f, 1f)]
    public float infoPanelAlpha = 0.6f;

    [Tooltip("Alt panel font boyutu (VR için 60+ önerilir)")]
    public float infoPanelFontSize = 72f;

    [Tooltip("Kalibrasyon sırasındaki metin rengi (sarı)")]
    public Color calibratingColor = new Color(1f, 0.85f, 0.1f);

    [Tooltip("Kalibrasyon tamamlandığında metin rengi (yeşil)")]
    public Color calibratedColor = new Color(0.3f, 1f, 0.3f);

    [Tooltip("Bağlantı bekleme rengi (gri)")]
    public Color waitingColor = new Color(0.8f, 0.8f, 0.8f);

    [Tooltip("Yüksek GSR değeri eşiği (µS) — üstündeyse değer kırmızıya döner")]
    public float highGsrThreshold = 15f;

    [Tooltip("GSR ondalık basamak sayısı")]
    [Range(0, 4)]
    public int gsrDecimalPlaces = 2;

    // === Merkez HUD (stres uyarısı) ===
    private GameObject _canvasObj;
    private Canvas _hudCanvas;
    private CanvasGroup _warningGroup;
    private RawImage _vignetteImage;
    private TextMeshProUGUI _warningText;
    private Texture2D _vignetteTexture;

    // === Alt bilgi paneli ===
    private GameObject _infoPanelObj;
    private CanvasGroup _infoPanelGroup;
    private Image _infoPanelBg;
    private TextMeshProUGUI _calibrationText;
    private Image _progressBarBg;
    private Image _progressBarFill;
    private TextMeshProUGUI _gsrText;
    private TextMeshProUGUI _predictionText;

    // === Durum: uyarı ===
    private float _warningTargetAlpha;
    private float _warningCurrentAlpha;

    // === Durum: alt panel ===
    private float _infoTargetAlpha;
    private float _infoCurrentAlpha;
    private bool _isCalibrated;
    private float _calibrationCompleteTime = -1f;
    private CalibrationInfo _lastCalibrationInfo;

    // === Test modu ===
    private float _testTimer;
    private bool _testStressState;

    // === Sabitler ===
    private const int VIGNETTE_TEXTURE_SIZE = 512;
    private const int UI_LAYER = 5;

    void Start()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;

        if (targetCamera == null)
        {
            Debug.LogError("[StressWarningHUD] Hedef kamera bulunamadı!");
            enabled = false;
            return;
        }

        CreateVignetteTexture();
        CreateHUD();

        SubscribeToDataSources();

        _warningTargetAlpha = 0f;
        _warningCurrentAlpha = 0f;
        _warningGroup.alpha = 0f;

        // Alt panel başlangıçta "Bağlantı bekleniyor..." ile görünür (kullanıcı durumu bilmeli).
        _infoTargetAlpha = ShouldShowInfoPanel() ? infoPanelAlpha : 0f;
        _infoCurrentAlpha = 0f;
        _infoPanelGroup.alpha = 0f;

        UpdateCalibrationUI(null);
    }

    /// <summary>
    /// Veri kaynaklarına abone olur (test modu ve canlı mod için ayrı mantık).
    /// </summary>
    private void SubscribeToDataSources()
    {
        if (testMode)
        {
            Debug.Log("[StressWarningHUD] TEST MODU aktif - rastgele stres değerleri üretilecek.");
            _testTimer = testInterval;
            // Test modunda da kalibrasyon simülasyonu başlatalım
            _lastCalibrationInfo = new CalibrationInfo
            {
                calibration_progress = 0f,
                calibration_total = 30f,
                is_calibrated = false,
                baseline = 0f,
                threshold = 0.5f
            };
            return;
        }

        if (stressPredictor == null)
            stressPredictor = FindAnyObjectByType<StressPredictor>();

        if (stressPredictor == null)
        {
            Debug.LogError("[StressWarningHUD] StressPredictor bulunamadı! Test modunu aktif edin.");
            enabled = false;
            return;
        }

        stressPredictor.OnPredictionReceived += OnPredictionReceived;
        stressPredictor.OnCalibrationProgress += OnCalibrationProgress;
        stressPredictor.OnCalibrationComplete += OnCalibrationComplete;

        // GSR collector opsiyonel (sadece showGsrValue açıksa gerekli)
        if (showGsrValue)
        {
            if (gsrDataCollector == null)
                gsrDataCollector = FindAnyObjectByType<GSRDataCollector>();

            if (gsrDataCollector != null)
            {
                gsrDataCollector.OnGSRValueUpdated += OnGSRValueUpdated;
                UpdateGsrUI(gsrDataCollector.GetLastGSRValue());
            }
            else
            {
                Debug.LogWarning("[StressWarningHUD] GSRDataCollector bulunamadı — GSR değeri gösterilemeyecek.");
            }
        }
    }

    void OnDestroy()
    {
        if (stressPredictor != null)
        {
            stressPredictor.OnPredictionReceived -= OnPredictionReceived;
            stressPredictor.OnCalibrationProgress -= OnCalibrationProgress;
            stressPredictor.OnCalibrationComplete -= OnCalibrationComplete;
        }

        if (gsrDataCollector != null)
            gsrDataCollector.OnGSRValueUpdated -= OnGSRValueUpdated;

        if (_vignetteTexture != null)
            Destroy(_vignetteTexture);
    }

    void Update()
    {
        // Test modu döngüsü — kalibrasyon ve stres simülasyonu
        if (testMode)
            UpdateTestMode();

        // Alt panel görünürlük mantığı (kalibrasyon tamamlandıktan N sn sonra fade out)
        UpdateInfoPanelVisibility();

        // Alpha geçişleri
        _warningCurrentAlpha = Mathf.MoveTowards(_warningCurrentAlpha, _warningTargetAlpha, Time.deltaTime * fadeSpeed);
        _warningGroup.alpha = _warningCurrentAlpha;

        _infoCurrentAlpha = Mathf.MoveTowards(_infoCurrentAlpha, _infoTargetAlpha, Time.deltaTime * fadeSpeed);
        _infoPanelGroup.alpha = _infoCurrentAlpha;

        // Canvas'ı kameranın önünde konumla (head-locked)
        if (_canvasObj != null && targetCamera != null)
        {
            Transform camT = targetCamera.transform;
            _canvasObj.transform.position = camT.position + camT.forward * canvasDistance;
            _canvasObj.transform.rotation = camT.rotation;
        }
    }

    /// <summary>
    /// Test modunda kalibrasyon ve stres durumunu simüle eder.
    /// </summary>
    private void UpdateTestMode()
    {
        // Kalibrasyon simülasyonu (ilk 10 saniye)
        if (!_isCalibrated && _lastCalibrationInfo != null)
        {
            _lastCalibrationInfo.calibration_progress = Mathf.Min(
                _lastCalibrationInfo.calibration_progress + Time.deltaTime,
                _lastCalibrationInfo.calibration_total);

            if (_lastCalibrationInfo.calibration_progress >= _lastCalibrationInfo.calibration_total)
            {
                _lastCalibrationInfo.is_calibrated = true;
                OnCalibrationComplete(_lastCalibrationInfo);
            }
            else
            {
                UpdateCalibrationUI(_lastCalibrationInfo);
            }
        }

        // Stres durumu simülasyonu (kalibrasyon bittikten sonra)
        if (_isCalibrated)
        {
            _testTimer -= Time.deltaTime;
            if (_testTimer <= 0f)
            {
                _testStressState = !_testStressState;
                _warningTargetAlpha = _testStressState ? 1f : 0f;
                _testTimer = testInterval;

                float prob = _testStressState ? Random.Range(0.6f, 0.95f) : Random.Range(0.05f, 0.35f);
                Debug.Log($"[StressWarningHUD][TEST] Durum: {(_testStressState ? "STRESLI" : "NORMAL")} | Olasılık: {prob:P0}");

                if (showPredictionText && _predictionText != null)
                {
                    _predictionText.text = _testStressState ? $"Stresli %{prob * 100:F0}" : $"Normal %{prob * 100:F0}";
                    _predictionText.color = _testStressState ? new Color(1f, 0.5f, 0.5f) : new Color(1f, 1f, 1f, 0.8f);
                }
            }
        }
    }

    /// <summary>
    /// Alt info panelinin görünmesi gerekip gerekmediğini belirler.
    /// Kurallar:
    ///   - Kalibrasyon devam ediyorsa → her zaman görünür (kullanıcı süreçte olduğunu bilmeli).
    ///   - showGsrValue veya showPredictionText açıksa → sürekli görünür.
    ///   - Aksi halde kalibrasyon bittikten calibrationHideDelay sn sonra gizlenir.
    /// </summary>
    private bool ShouldShowInfoPanel()
    {
        if (!showCalibrationProgress && !showGsrValue && !showPredictionText)
            return false;

        // Kalibrasyon devam ediyor → göster
        if (showCalibrationProgress && !_isCalibrated)
            return true;

        // Canlı veri gösteriliyor → sürekli göster
        if (showGsrValue || showPredictionText)
            return true;

        // Kalibrasyon yeni bitti → geçici olarak göster
        if (_isCalibrated && _calibrationCompleteTime > 0f)
        {
            float elapsed = Time.time - _calibrationCompleteTime;
            return elapsed < calibrationHideDelay;
        }

        return false;
    }

    private void UpdateInfoPanelVisibility()
    {
        _infoTargetAlpha = ShouldShowInfoPanel() ? infoPanelAlpha : 0f;
    }

    // ==========================================================================================
    // === Vignette + HUD kuruluşu
    // ==========================================================================================

    /// <summary>
    /// Radial gradient vignette dokusu oluşturur (saydam merkez → sarı kenar).
    /// </summary>
    private void CreateVignetteTexture()
    {
        _vignetteTexture = new Texture2D(VIGNETTE_TEXTURE_SIZE, VIGNETTE_TEXTURE_SIZE, TextureFormat.RGBA32, false);
        _vignetteTexture.wrapMode = TextureWrapMode.Clamp;
        _vignetteTexture.filterMode = FilterMode.Bilinear;

        float cx = VIGNETTE_TEXTURE_SIZE * 0.5f;
        float cy = VIGNETTE_TEXTURE_SIZE * 0.5f;

        Color[] pixels = new Color[VIGNETTE_TEXTURE_SIZE * VIGNETTE_TEXTURE_SIZE];
        for (int y = 0; y < VIGNETTE_TEXTURE_SIZE; y++)
        {
            for (int x = 0; x < VIGNETTE_TEXTURE_SIZE; x++)
            {
                float dx = (x - cx) / cx;
                float dy = (y - cy) / cy;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                float t = Mathf.InverseLerp(vignetteInnerRadius, vignetteOuterRadius, dist);
                t = t * t;

                Color c = vignetteColor;
                c.a = vignetteColor.a * t;
                pixels[y * VIGNETTE_TEXTURE_SIZE + x] = c;
            }
        }

        _vignetteTexture.SetPixels(pixels);
        _vignetteTexture.Apply();
    }

    /// <summary>
    /// Root-level World Space Canvas HUD'u oluşturur. Merkezde uyarı, altta bilgi paneli.
    /// </summary>
    private void CreateHUD()
    {
        _canvasObj = new GameObject("StressWarningHUD_Canvas");

        _hudCanvas = _canvasObj.AddComponent<Canvas>();
        _hudCanvas.renderMode = RenderMode.WorldSpace;
        _hudCanvas.sortingOrder = 999;

        // Canvas boyutunu kameranın frustum'una göre hesapla
        float vFov = targetCamera.fieldOfView * Mathf.Deg2Rad;
        float frustumH = 2f * canvasDistance * Mathf.Tan(vFov * 0.5f);
        float frustumW = frustumH * targetCamera.aspect;
        float worldW = frustumW * 1.1f;
        float worldH = frustumH * 1.1f;

        RectTransform canvasRect = _hudCanvas.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(worldW * 1000f, worldH * 1000f);
        canvasRect.localScale = Vector3.one * 0.001f;

        CreateWarningLayer();
        CreateInfoPanel();

        // Tüm objeleri UI layer'ına (5) ayarla
        SetLayerRecursive(_canvasObj, UI_LAYER);

        Debug.Log($"[StressWarningHUD] HUD oluşturuldu (World Space, {worldW:F2}x{worldH:F2}m, mesafe: {canvasDistance}m).");
    }

    /// <summary>
    /// Stres uyarısı katmanını oluşturur: vignette + merkez yazı.
    /// </summary>
    private void CreateWarningLayer()
    {
        GameObject warningRoot = new GameObject("WarningLayer");
        warningRoot.transform.SetParent(_canvasObj.transform, false);
        _warningGroup = warningRoot.AddComponent<CanvasGroup>();
        _warningGroup.interactable = false;
        _warningGroup.blocksRaycasts = false;

        RectTransform warningRect = warningRoot.GetComponent<RectTransform>();
        if (warningRect == null) warningRect = warningRoot.AddComponent<RectTransform>();
        warningRect.anchorMin = Vector2.zero;
        warningRect.anchorMax = Vector2.one;
        warningRect.offsetMin = Vector2.zero;
        warningRect.offsetMax = Vector2.zero;

        // Vignette overlay
        GameObject vignetteObj = new GameObject("VignetteOverlay");
        vignetteObj.transform.SetParent(warningRoot.transform, false);
        _vignetteImage = vignetteObj.AddComponent<RawImage>();
        _vignetteImage.texture = _vignetteTexture;
        _vignetteImage.raycastTarget = false;
        StretchToParent(_vignetteImage.rectTransform);

        // Metin arka planı
        GameObject textBgObj = new GameObject("TextBackground");
        textBgObj.transform.SetParent(warningRoot.transform, false);
        Image textBg = textBgObj.AddComponent<Image>();
        textBg.color = new Color(0f, 0f, 0f, 0.5f);
        textBg.raycastTarget = false;
        SetAnchors(textBg.rectTransform, new Vector2(0.25f, 0.78f), new Vector2(0.75f, 0.90f));

        // Uyarı metni (merkez-üst)
        GameObject textObj = new GameObject("WarningText");
        textObj.transform.SetParent(warningRoot.transform, false);
        _warningText = textObj.AddComponent<TextMeshProUGUI>();
        _warningText.text = warningMessage;
        _warningText.color = textColor;
        _warningText.fontSize = fontSize;
        _warningText.alignment = TextAlignmentOptions.Center;
        _warningText.fontStyle = FontStyles.Bold;
        _warningText.raycastTarget = false;
        _warningText.enableWordWrapping = true;
        SetAnchors(_warningText.rectTransform, new Vector2(0.25f, 0.78f), new Vector2(0.75f, 0.90f));
    }

    /// <summary>
    /// Alt bilgi panelini oluşturur: kalibrasyon progress bar, GSR değeri, tahmin metni.
    /// Alt-orta bölgede küçük ve saydam — VR'da merkez görüşü engellemez.
    /// </summary>
    private void CreateInfoPanel()
    {
        _infoPanelObj = new GameObject("InfoPanel");
        _infoPanelObj.transform.SetParent(_canvasObj.transform, false);

        _infoPanelGroup = _infoPanelObj.AddComponent<CanvasGroup>();
        _infoPanelGroup.interactable = false;
        _infoPanelGroup.blocksRaycasts = false;

        // Alt-orta: dikey %15-%35 (yerden yukarı), yatayda %22-%78 (yaklaşık yarım genişlik).
        // Merkez görüşü engellemeyecek kadar aşağıda ama kafa hafif eğilmeden rahat okunacak kadar yukarıda.
        RectTransform panelRect = _infoPanelObj.AddComponent<RectTransform>();
        SetAnchors(panelRect, new Vector2(0.22f, 0.15f), new Vector2(0.78f, 0.35f));

        // Saydam koyu arka plan
        _infoPanelBg = _infoPanelObj.AddComponent<Image>();
        _infoPanelBg.color = new Color(0f, 0f, 0f, 0.45f);
        _infoPanelBg.raycastTarget = false;

        // Kalibrasyon durum metni (panelin üst kısmı)
        GameObject calTextObj = new GameObject("CalibrationText");
        calTextObj.transform.SetParent(_infoPanelObj.transform, false);
        _calibrationText = calTextObj.AddComponent<TextMeshProUGUI>();
        _calibrationText.text = "Bağlantı bekleniyor...";
        _calibrationText.color = waitingColor;
        _calibrationText.fontSize = infoPanelFontSize;
        _calibrationText.fontStyle = FontStyles.Bold;
        _calibrationText.alignment = TextAlignmentOptions.Center;
        _calibrationText.raycastTarget = false;
        _calibrationText.enableWordWrapping = false;
        // Auto-size: rect'e göre otomatik olarak büyütür; font boyutu minimum için taban.
        _calibrationText.enableAutoSizing = true;
        _calibrationText.fontSizeMin = infoPanelFontSize;
        _calibrationText.fontSizeMax = infoPanelFontSize * 2f;
        SetAnchors(_calibrationText.rectTransform, new Vector2(0.05f, 0.60f), new Vector2(0.95f, 1.00f));

        // Progress bar arka planı
        GameObject progressBgObj = new GameObject("ProgressBarBg");
        progressBgObj.transform.SetParent(_infoPanelObj.transform, false);
        _progressBarBg = progressBgObj.AddComponent<Image>();
        _progressBarBg.color = new Color(1f, 1f, 1f, 0.15f);
        _progressBarBg.raycastTarget = false;
        SetAnchors(_progressBarBg.rectTransform, new Vector2(0.10f, 0.45f), new Vector2(0.90f, 0.55f));

        // Progress bar doldurma
        GameObject progressFillObj = new GameObject("ProgressBarFill");
        progressFillObj.transform.SetParent(progressBgObj.transform, false);
        _progressBarFill = progressFillObj.AddComponent<Image>();
        _progressBarFill.color = calibratingColor;
        _progressBarFill.type = Image.Type.Filled;
        _progressBarFill.fillMethod = Image.FillMethod.Horizontal;
        _progressBarFill.fillAmount = 0f;
        _progressBarFill.raycastTarget = false;
        StretchToParent(_progressBarFill.rectTransform);

        // GSR değeri (opsiyonel, alt-sol köşe — açıldığında tahmin metniyle yan yana durur)
        GameObject gsrObj = new GameObject("GsrText");
        gsrObj.transform.SetParent(_infoPanelObj.transform, false);
        _gsrText = gsrObj.AddComponent<TextMeshProUGUI>();
        _gsrText.text = showGsrValue ? "GSR: --" : "";
        _gsrText.color = new Color(1f, 1f, 1f, 0.85f);
        _gsrText.fontSize = infoPanelFontSize * 0.8f;
        _gsrText.alignment = TextAlignmentOptions.MidlineLeft;
        _gsrText.raycastTarget = false;
        _gsrText.enableWordWrapping = false;
        // GSR açıksa alt bandı ikiye bölüp solda göster; kapalıyken zaten gizli.
        Vector2 gsrMin = showGsrValue ? new Vector2(0.05f, 0.05f) : new Vector2(0.00f, 0.00f);
        Vector2 gsrMax = showGsrValue ? new Vector2(0.48f, 0.40f) : new Vector2(0.00f, 0.00f);
        SetAnchors(_gsrText.rectTransform, gsrMin, gsrMax);
        gsrObj.SetActive(showGsrValue);

        // Tahmin metni (sürekli görünür — alt-orta, geniş alan, büyük font)
        GameObject predObj = new GameObject("PredictionText");
        predObj.transform.SetParent(_infoPanelObj.transform, false);
        _predictionText = predObj.AddComponent<TextMeshProUGUI>();
        _predictionText.text = "--";
        _predictionText.color = new Color(1f, 1f, 1f, 0.9f);
        _predictionText.fontSize = infoPanelFontSize;      // ana bilgi: tam boy
        _predictionText.fontStyle = FontStyles.Bold;
        _predictionText.raycastTarget = false;
        _predictionText.enableWordWrapping = false;
        // GSR kapalıysa tüm alt şeridi kapla (ortada), açıksa sağ yarıda
        Vector2 predMin = showGsrValue ? new Vector2(0.52f, 0.05f) : new Vector2(0.05f, 0.05f);
        Vector2 predMax = showGsrValue ? new Vector2(0.95f, 0.40f) : new Vector2(0.95f, 0.40f);
        _predictionText.alignment = showGsrValue ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Center;
        SetAnchors(_predictionText.rectTransform, predMin, predMax);
        predObj.SetActive(showPredictionText);
    }

    // ==========================================================================================
    // === Event handler'lar
    // ==========================================================================================

    /// <summary>
    /// Stres tahmini geldiğinde çağrılır — merkez uyarı görünürlüğünü ve alt paneldeki detay metnini günceller.
    /// </summary>
    private void OnPredictionReceived(StressResult result)
    {
        _warningTargetAlpha = result.stres == 1 ? 1f : 0f;

        if (result.stres == 1)
            Debug.Log($"[StressWarningHUD] Stres algılandı! Olasılık: {result.stres_olasilik:P0}");

        if (showPredictionText && _predictionText != null)
        {
            string status = result.stres == 1 ? "Stresli" : "Normal";
            _predictionText.text = $"{status} %{result.stres_olasilik * 100f:F0}";
            _predictionText.color = result.stres == 1
                ? new Color(1f, 0.5f, 0.5f)
                : new Color(1f, 1f, 1f, 0.8f);
        }
    }

    /// <summary>
    /// Kalibrasyon ilerlediğinde çağrılır.
    /// </summary>
    private void OnCalibrationProgress(CalibrationInfo info)
    {
        _lastCalibrationInfo = info;
        UpdateCalibrationUI(info);
    }

    /// <summary>
    /// Kalibrasyon tamamlandığında çağrılır — "Hazır" durumunu göster, geri sayımı başlat.
    /// </summary>
    private void OnCalibrationComplete(CalibrationInfo info)
    {
        _isCalibrated = true;
        _lastCalibrationInfo = info;
        _calibrationCompleteTime = Time.time;
        UpdateCalibrationUI(info);

        Debug.Log($"[StressWarningHUD] Kalibrasyon tamamlandı. Baseline: {info.baseline:P1}, Threshold: {info.threshold:P1}");
    }

    /// <summary>
    /// GSR değeri güncellendiğinde çağrılır (showGsrValue açıksa).
    /// </summary>
    private void OnGSRValueUpdated(float gsrValue)
    {
        UpdateGsrUI(gsrValue);
    }

    // ==========================================================================================
    // === UI güncelleme
    // ==========================================================================================

    /// <summary>
    /// Kalibrasyon metni, rengi ve progress bar'ını günceller.
    /// </summary>
    private void UpdateCalibrationUI(CalibrationInfo info)
    {
        if (_calibrationText == null)
            return;

        if (info == null)
        {
            _calibrationText.text = "Bağlantı bekleniyor...";
            _calibrationText.color = waitingColor;
            if (_progressBarFill != null)
                _progressBarFill.fillAmount = 0f;
            return;
        }

        if (info.is_calibrated)
        {
            _calibrationText.text = $"Hazır — Eşik: %{info.threshold * 100f:F0}";
            _calibrationText.color = calibratedColor;
            if (_progressBarFill != null)
            {
                _progressBarFill.fillAmount = 1f;
                _progressBarFill.color = calibratedColor;
            }
        }
        else
        {
            float progress = info.calibration_total > 0f
                ? Mathf.Clamp01(info.calibration_progress / info.calibration_total)
                : 0f;
            _calibrationText.text = $"Kalibrasyon {info.calibration_progress:F0}/{info.calibration_total:F0}s";
            _calibrationText.color = calibratingColor;
            if (_progressBarFill != null)
            {
                _progressBarFill.fillAmount = progress;
                _progressBarFill.color = calibratingColor;
            }
        }
    }

    /// <summary>
    /// GSR değerini ve rengini (yüksek eşik kontrolü) günceller.
    /// </summary>
    private void UpdateGsrUI(float gsrValue)
    {
        if (!showGsrValue || _gsrText == null)
            return;

        string format = "F" + gsrDecimalPlaces;
        _gsrText.text = $"GSR: {gsrValue.ToString(format)} µS";
        _gsrText.color = gsrValue >= highGsrThreshold
            ? new Color(1f, 0.4f, 0.4f, 0.9f)
            : new Color(1f, 1f, 1f, 0.85f);
    }

    // ==========================================================================================
    // === Yardımcılar
    // ==========================================================================================

    private static void StretchToParent(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void SetAnchors(RectTransform rt, Vector2 min, Vector2 max)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void SetLayerRecursive(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
            SetLayerRecursive(child.gameObject, layer);
    }
}
