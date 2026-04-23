/*
 * Unity için LightGBM WebSocket Client Örneği
 * 
 * Kullanım:
 * 1. NativeWebSocket paketini Unity'ye ekleyin (Package Manager > Add package from git URL)
 *    https://github.com/endel/NativeWebSocket.git
 * 2. Bu scripti bir GameObject'e ekleyin
 * 3. Özellik değerlerini SetFeature() metoduyla ayarlayın
 * 4. PredictStress() metodunu çağırarak tahmin yapın
 */

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using NativeWebSocket;

public class StressPredictor : MonoBehaviour
{
    [Header("Sunucu Ayarları")]
    [Tooltip("Python WebSocket sunucusunun adresi")]
    public string serverUrl = "ws://localhost:8765";
    
    [Header("Debug")]
    [Tooltip("Bağlantı durumunu göster")]
    public bool showDebugLogs = true;
    
    private WebSocket websocket;
    private Dictionary<string, float> features = new Dictionary<string, float>();
    
    // Event'ler - Unity'deki diğer scriptler dinleyebilir
    public event Action<StressResult> OnPredictionReceived;
    public event Action<string> OnError;
    
    async void Start()
    {
        // WebSocket bağlantısını başlat
        websocket = new WebSocket(serverUrl);
        
        // Event handler'ları ayarla
        websocket.OnOpen += OnWebSocketOpen;
        websocket.OnError += OnWebSocketError;
        websocket.OnClose += OnWebSocketClose;
        websocket.OnMessage += OnWebSocketMessage;
        
        // Bağlan
        await websocket.Connect();
    }
    
    void OnWebSocketOpen()
    {
        if (showDebugLogs)
            Debug.Log("[StressPredictor] Python sunucusuna bağlandı!");
    }
    
    void OnWebSocketError(string error)
    {
        Debug.LogError($"[StressPredictor] WebSocket hatası: {error}");
        OnError?.Invoke(error);
    }
    
    void OnWebSocketClose(WebSocketCloseCode closeCode)
    {
        if (showDebugLogs)
            Debug.Log($"[StressPredictor] Bağlantı kapatıldı: {closeCode}");
    }
    
    void OnWebSocketMessage(byte[] bytes)
    {
        string message = System.Text.Encoding.UTF8.GetString(bytes);
        
        if (showDebugLogs)
            Debug.Log($"[StressPredictor] Sunucudan gelen: {message}");
        
        try
        {
            // JSON'u parse et
            StressResult result = JsonUtility.FromJson<StressResult>(message);
            
            if (result.basari)
            {
                if (showDebugLogs)
                {
                    Debug.Log($"[StressPredictor] Tahmin: {result.durum}");
                    Debug.Log($"[StressPredictor] Stres Olasılığı: {result.stres_olasilik:P2}");
                }
                
                // Event'i tetikle
                OnPredictionReceived?.Invoke(result);
            }
            else
            {
                Debug.LogError($"[StressPredictor] Hata: {result.error}");
                OnError?.Invoke(result.error);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[StressPredictor] JSON parse hatası: {e.Message}");
            OnError?.Invoke($"JSON parse hatası: {e.Message}");
        }
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
    /// Stres tahmini yap
    /// </summary>
    public async void PredictStress()
    {
        if (websocket == null || websocket.State != WebSocketState.Open)
        {
            Debug.LogWarning("[StressPredictor] WebSocket bağlantısı açık değil!");
            return;
        }
        
        if (features.Count == 0)
        {
            Debug.LogWarning("[StressPredictor] Özellik verisi yok!");
            return;
        }
        
        // Dictionary'yi JSON'a çevir
        // Unity'nin JsonUtility'si Dictionary'yi desteklemediği için
        // manuel olarak JSON string oluşturuyoruz
        string json = DictionaryToJson(features);
        
        if (showDebugLogs)
            Debug.Log($"[StressPredictor] Tahmin gönderiliyor: {features.Count} özellik");
        
        await websocket.SendText(json);
    }
    
    /// <summary>
    /// Dictionary'yi JSON string'e çevir
    /// </summary>
    private string DictionaryToJson(Dictionary<string, float> dict)
    {
        var jsonParts = new List<string>();
        foreach (var kvp in dict)
        {
            jsonParts.Add($"\"{kvp.Key}\": {kvp.Value}");
        }
        return "{" + string.Join(", ", jsonParts) + "}";
    }
    
    void Update()
    {
        #if !UNITY_WEBGL || UNITY_EDITOR
        // WebSocket mesajlarını işle
        websocket?.DispatchMessageQueue();
        #endif
    }
    
    async void OnApplicationQuit()
    {
        if (websocket != null)
        {
            await websocket.Close();
        }
    }
    
    async void OnDestroy()
    {
        if (websocket != null)
        {
            await websocket.Close();
        }
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
 * // Tahmin yap
 * predictor.PredictStress();
 */

