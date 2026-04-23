"""
WebSocket sunucusunu test etmek için basit bir client scripti.
Sunucunun çalışıp çalışmadığını ve tahmin yapıp yapmadığını test eder.
"""
import asyncio
import websockets
import json
import pandas as pd

# Test verisi - modelin beklediği tüm özellikler
# Bu değerler örnek değerlerdir, gerçek verilerle değiştirilmelidir
async def test_websocket():
    """WebSocket sunucusunu test et"""
    uri = "ws://localhost:8765"
    
    print("=" * 50)
    print("WEBSOCKET TEST")
    print("=" * 50)
    print(f"Sunucuya bağlanılıyor: {uri}")
    
    try:
        async with websockets.connect(uri) as websocket:
            print("✓ Bağlantı kuruldu!")
            
            # Örnek test verisi (v3 model: 16 özellik)
            # Gerçek kullanımda Unity'den gelen veriler kullanılacak
            # Log dönüşüm sunucu tarafında otomatik uygulanır
            test_data = {
                "Number of Peaks": 15.0,
                "SD_Saccade_Direction": 15.2,
                "Mean_Blink_Duration": 150.0,
                "Num_of_Blink": 12.5,
                "Skew_Microsac_V_Amp": 0.15,
                "Max_Microsac_Dir": 110.0,
                "Max_Saccade_Direction": 120.0,
                "Mean_Microsac_H_Amp": 1.5,
                "Num_of_Fixations": 45.2,
                "Mean_Saccade_Direction": 90.0,
                "SD_Microsac_V_Amp": 0.8,
                "Skew_Saccade_Direction": 1.2,
                "Mean_Saccade_Duration": 45.3,
                "SD": 1.8,
                "Max_Microsac_V_Amp": 2.5,
                "Skew_Fixation_Duration": 0.8
            }
            
            # Veriyi gönder
            message = json.dumps(test_data)
            print(f"\nGönderilen veri ({len(test_data)} özellik):")
            print(json.dumps(test_data, indent=2, ensure_ascii=False))
            
            await websocket.send(message)
            print("\n✓ Veri gönderildi, yanıt bekleniyor...")
            
            # Yanıtı al
            response = await websocket.recv()
            result = json.loads(response)
            
            print("\n" + "=" * 50)
            print("SONUÇ")
            print("=" * 50)
            print(json.dumps(result, indent=2, ensure_ascii=False))
            
            if result.get("basari", False):
                print(f"\n✓ Tahmin başarılı!")
                print(f"  Durum: {result['durum']}")
                print(f"  Stres Olasılığı: {result['stres_olasilik']:.2%}")
                print(f"  Normal Olasılığı: {result['stres_yok_olasilik']:.2%}")
            else:
                print(f"\n✗ Hata: {result.get('error', 'Bilinmeyen hata')}")
            
    except ConnectionRefusedError:
        print("\n✗ Bağlantı reddedildi!")
        print("  Sunucunun çalıştığından emin olun: python websocket_server.py")
    except websockets.exceptions.ConnectionClosedError as e:
        print(f"\n✗ Bağlantı kapatıldı: {e}")
        print("  Sunucu hatası olabilir, logları kontrol edin.")
    except Exception as e:
        print(f"\n✗ Hata: {e}")

if __name__ == "__main__":
    asyncio.run(test_websocket())

