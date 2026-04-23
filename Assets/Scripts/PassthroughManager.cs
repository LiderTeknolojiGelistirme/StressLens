using UnityEngine;
using VIVE.OpenXR;
using VIVE.OpenXR.CompositionLayer;
using VIVE.OpenXR.Passthrough;

/// <summary>
/// MR modu için tam ekran passthrough yönetimi.
/// Underlay modunda gerçek dünya sanal içeriğin arkasında görünür,
/// sanal nesneler gerçek dünyanın üzerinde render edilir.
/// </summary>
public class PassthroughManager : MonoBehaviour
{
    [Header("Passthrough Ayarları")]
    [Tooltip("Passthrough katman tipi: Underlay = gerçek dünya arkada, Overlay = gerçek dünya önde")]
    public LayerType layerType = LayerType.Underlay;

    [Tooltip("Passthrough saydamlık değeri (0.0 = tamamen saydam, 1.0 = tamamen opak)")]
    [Range(0f, 1f)]
    public float alpha = 1.0f;

    [Tooltip("Uygulama başladığında passthrough otomatik başlasın mı?")]
    public bool autoStart = true;

    private VIVE.OpenXR.Passthrough.XrPassthroughHTC passthroughHandle;
    private bool isPassthroughActive = false;

    void Start()
    {
        if (autoStart)
        {
            EnablePassthrough();
        }
    }

    /// <summary>
    /// Passthrough modunu aktif eder.
    /// Kameranın Clear Flags = Solid Color ve Background = (0,0,0,0) olmalıdır.
    /// </summary>
    public void EnablePassthrough()
    {
        if (isPassthroughActive)
            return;

        // Kamera ayarlarını doğrula ve düzelt
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            mainCam.clearFlags = CameraClearFlags.SolidColor;
            mainCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        }

        // Planar passthrough oluştur (tam ekran)
        var result = PassthroughAPI.CreatePlanarPassthrough(
            out passthroughHandle,
            layerType,
            alpha: alpha);

        if (result == XrResult.XR_SUCCESS)
        {
            isPassthroughActive = true;
            Debug.Log("[PassthroughManager] MR modu aktif — LayerType: " + layerType);
        }
        else
        {
            Debug.LogWarning("[PassthroughManager] Passthrough oluşturulamadı (XrResult: " + result + "). " +
                "OpenXR ayarlarında VIVE XR Passthrough ve VIVE XR Composition Layer aktif mi kontrol edin.");
        }
    }

    /// <summary>
    /// Passthrough modunu kapatır (VR moduna döner).
    /// </summary>
    public void DisablePassthrough()
    {
        if (!isPassthroughActive)
            return;

        PassthroughAPI.DestroyPassthrough(passthroughHandle);
        isPassthroughActive = false;
        Debug.Log("[PassthroughManager] MR modu kapatıldı — VR moduna dönüldü.");
    }

    /// <summary>
    /// Passthrough saydamlığını çalışma zamanında değiştirir.
    /// </summary>
    public void SetAlpha(float newAlpha)
    {
        alpha = Mathf.Clamp01(newAlpha);
        if (isPassthroughActive)
        {
            PassthroughAPI.SetPassthroughAlpha(passthroughHandle, alpha);
        }
    }

    /// <summary>
    /// MR ve VR modları arasında geçiş yapar.
    /// </summary>
    public void TogglePassthrough()
    {
        if (isPassthroughActive)
            DisablePassthrough();
        else
            EnablePassthrough();
    }

    void OnDestroy()
    {
        if (isPassthroughActive)
        {
            PassthroughAPI.DestroyPassthrough(passthroughHandle);
        }
    }
}
