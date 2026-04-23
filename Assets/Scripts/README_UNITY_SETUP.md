# Unity WebSocket Kurulum Kılavuzu

## NativeWebSocket Paketi Yükleme

Bu script, **NativeWebSocket** paketini gerektirir. Paketi yüklemek için aşağıdaki adımları izleyin:

### Adım 1: Package Manager'ı Açın
1. Unity Editor'da **Window** > **Package Manager** menüsünü açın
2. Veya **Ctrl+9** (Windows) / **Cmd+9** (Mac) kısayolunu kullanın

### Adım 2: Paketi Ekleyin
1. Package Manager penceresinin sol üst köşesindeki **"+"** butonuna tıklayın
2. Açılan menüden **"Add package from git URL"** seçeneğini seçin
3. Şu URL'yi girin:
   ```
   https://github.com/endel/NativeWebSocket.git
   ```
4. **"Add"** butonuna tıklayın
5. Paketin yüklenmesini bekleyin (birkaç saniye sürebilir)

### Adım 3: Doğrulama
1. Package Manager'da **"In Project"** sekmesine gidin
2. **"Native Web Socket"** paketinin listelendiğini kontrol edin
3. Eğer paket görünüyorsa, kurulum başarılıdır!

## Alternatif: Manuel Paket Yükleme

Eğer yukarıdaki yöntem çalışmazsa:

1. Unity projenizin kök dizininde `Packages` klasörünü açın
2. `manifest.json` dosyasını bir metin editörü ile açın
3. `dependencies` bölümüne şu satırı ekleyin:
   ```json
   "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git",
   ```
4. Dosyayı kaydedin
5. Unity Editor'a geri dönün - Unity otomatik olarak paketi yükleyecektir

## Sorun Giderme

### Hata: "The type or namespace name 'NativeWebSocket' could not be found"
- **Çözüm**: Paket henüz yüklenmemiş. Yukarıdaki adımları tekrar izleyin.
- Paket yüklendikten sonra Unity Editor'ı yeniden başlatın.

### Hata: "Package not found" veya "Git URL error"
- **Çözüm**: İnternet bağlantınızı kontrol edin
- Unity'nin git erişimi olduğundan emin olun
- Alternatif olarak, paketi manuel olarak indirip projeye ekleyebilirsiniz

### Paket yüklendi ama hala hata alıyorum
- Unity Editor'ı kapatıp yeniden açın
- **Assets** > **Reimport All** menüsünü deneyin
- Script dosyasını tekrar açıp kaydedin

## Paket Versiyonu

Bu script **NativeWebSocket v1.0.0** veya üzeri versiyonlarla uyumludur.

## Daha Fazla Bilgi

NativeWebSocket paketi hakkında daha fazla bilgi için:
- GitHub: https://github.com/endel/NativeWebSocket
- Unity Forum: Unity WebSocket tartışmaları

## Sonraki Adımlar

Paket yüklendikten sonra:
1. `StressPredictor` scriptini bir GameObject'e ekleyin
2. Inspector'da sunucu URL'ini ayarlayın (varsayılan: `ws://localhost:8765`)
3. Python WebSocket sunucusunu başlatın
4. `SetFeature()` metodunu kullanarak özellikleri ayarlayın
5. `PredictStress()` metodunu çağırarak tahmin yapın

