using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Calisma zamaninda ve editorde gereksiz bilesenleri devre disi birakarak
/// tekrarlayan hata loglarini ve CPU israfini onler.
///
/// Sorun 1 - PostProcessLayer: Kamera uzerindeki PostProcessLayer,
///   AmbientOcclusion efektinde NullReferenceException uretiyor (edit + play mode).
///   XR projesinde PostProcessing kullanilmadigindan devre disi birakilir.
///
/// Sorun 2 - XRInputModalityManager: Her karede Hand Tracking Subsystem
///   aranmaya calisiliyor ancak OpenXR ayarlarinda devre disi (sadece play mode).
///   Gereksiz hata loglari ve CPU kullanimi onlenir.
/// </summary>
[DefaultExecutionOrder(-100)]
public class RuntimeFixups : MonoBehaviour
{
    /// <summary>
    /// Play mode basladiginda sorunlu bilesenleri devre disi birakir.
    /// </summary>
    private void Awake()
    {
        DisablePostProcessLayers();
        DisableXRInputModalityManagers();
    }

    private static void DisablePostProcessLayers()
    {
#if UNITY_POST_PROCESSING_STACK_V2
        var layers = Object.FindObjectsByType<UnityEngine.Rendering.PostProcessing.PostProcessLayer>(FindObjectsSortMode.None);
        foreach (var layer in layers)
        {
            layer.enabled = false;
            Debug.Log("[RuntimeFixups] PostProcessLayer devre disi birakildi: " + layer.gameObject.name);
        }
#endif
    }

    private static void DisableXRInputModalityManagers()
    {
        var managers = Object.FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Inputs.XRInputModalityManager>(FindObjectsSortMode.None);
        foreach (var mgr in managers)
        {
            mgr.enabled = false;
            Debug.Log("[RuntimeFixups] XRInputModalityManager devre disi birakildi: " + mgr.gameObject.name);
        }
    }
}

#if UNITY_EDITOR
/// <summary>
/// Editor mode'da (play mode disinda) PostProcessLayer NullRef hatasini onler.
/// Scene/Game view kameralari edit mode'da da render edilir, bu nedenle
/// PostProcessLayer edit mode'da da devre disi birakilmali.
/// </summary>
[InitializeOnLoad]
public static class EditorRuntimeFixups
{
    static EditorRuntimeFixups()
    {
        // Editor yuklendikten sonra ilk update'te calistir
        EditorApplication.delayCall += DisablePostProcessLayersInEditor;
        // Sahne degistiginde tekrar calistir
        EditorApplication.hierarchyChanged += DisablePostProcessLayersInEditor;
    }

    private static void DisablePostProcessLayersInEditor()
    {
        // Play mode'da RuntimeFixups.Awake() zaten hallediyor
        if (EditorApplication.isPlaying)
            return;

#if UNITY_POST_PROCESSING_STACK_V2
        var layers = Object.FindObjectsByType<UnityEngine.Rendering.PostProcessing.PostProcessLayer>(FindObjectsSortMode.None);
        foreach (var layer in layers)
        {
            if (layer.enabled)
            {
                layer.enabled = false;
                Debug.Log("[EditorRuntimeFixups] Edit mode'da PostProcessLayer devre disi birakildi: " + layer.gameObject.name);
            }
        }
#endif
    }
}
#endif
