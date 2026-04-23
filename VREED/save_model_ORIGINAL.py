"""
LightGBM modelini kaydetmek için script.
Notebook'taki veri hazırlama ve model eğitme adımlarını tekrarlar.
"""
import pickle
import json
import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.model_selection import train_test_split
from pathlib import Path

# Model klasörünü oluştur
model_dir = Path('model')
model_dir.mkdir(exist_ok=True)

print("=" * 50)
print("VERİ YÜKLEME")
print("=" * 50)

# Veri setlerini yükle
eye_df = pd.read_csv('EyeTracking_FeaturesExtracted.csv')
gsr_df = pd.read_csv('GSR_FeaturesExtracted.csv')
print("✓ EyeTracking ve GSR veri setleri yüklendi.")

print("\n" + "=" * 50)
print("VERİ HAZIRLAMA")
print("=" * 50)

# Etiketleme: Quad_Cat == 3 -> stres = 1, diğerleri -> stres = 0
eye_df['stres'] = eye_df['Quad_Cat'].apply(lambda x: 1 if x == 3 else 0)
gsr_df['stres'] = gsr_df['Quad_Cat'].apply(lambda x: 1 if x == 3 else 0)
print("✓ Etiketleme tamamlandı.")

# Quad_Cat sütununu sil
eye_df = eye_df.drop(columns=['Quad_Cat'])
gsr_df = gsr_df.drop(columns=['Quad_Cat'])

# GSR'dan sadece feature'ları al (stres sütunu tekrar etmesin)
gsr_features_only = gsr_df.drop(columns=['stres'])

# İki veri setini birleştir
birlesik_df = pd.concat([eye_df, gsr_features_only], axis=1)
print(f"✓ Veri setleri birleştirildi. Shape: {birlesik_df.shape}")

print("\n" + "=" * 50)
print("KORELASYON ANALİZİ VE ÖZELLİK SEÇİMİ")
print("=" * 50)

# Yüksek korelasyonlu özellikleri sil (threshold = 0.8)
threshold = 0.8
corr_matrix = birlesik_df.select_dtypes(include=[np.number]).corr().abs()

# Üst üçgen maskesi
upper_triangle = corr_matrix.where(
    np.triu(np.ones(corr_matrix.shape), k=1).astype(bool)
)

# Eşik üstü korelasyonlu özellikleri bul
to_drop = [
    column
    for column in upper_triangle.columns
    if any(upper_triangle[column] >= threshold)
]

birlesik_df_reduced = birlesik_df.drop(columns=to_drop)
print(f"✓ {len(to_drop)} adet yüksek korelasyonlu özellik silindi.")
print(f"✓ Yeni shape: {birlesik_df_reduced.shape}")

print("\n" + "=" * 50)
print("NaN DEĞERLERİNİ TEMİZLEME")
print("=" * 50)

# NaN değerleri sil
df_silinmis = birlesik_df_reduced.dropna()
print(f"✓ NaN değerler temizlendi.")
print(f"✓ Final shape: {df_silinmis.shape}")
print(f"✓ Silinen satır sayısı: {birlesik_df_reduced.shape[0] - df_silinmis.shape[0]}")

print("\n" + "=" * 50)
print("VERİ AYIRMA")
print("=" * 50)

# X ve y'yi ayır
X = df_silinmis.drop(columns=["stres"])
y = df_silinmis["stres"]

# Train-test split
X_train, X_test, y_train, y_test = train_test_split(
    X, y,
    test_size=0.2,
    random_state=42,
    stratify=y
)
print(f"✓ Train set: {X_train.shape[0]} örnek")
print(f"✓ Test set: {X_test.shape[0]} örnek")

print("\n" + "=" * 50)
print("MODEL EĞİTİMİ")
print("=" * 50)

# LightGBM modelini eğit (notebook'taki varsayılan ayarlar)
model = lgb.LGBMClassifier(random_state=42)
model.fit(X_train, y_train)
print("✓ LightGBM modeli eğitildi.")

# Test performansını göster
from sklearn.metrics import accuracy_score, f1_score, roc_auc_score, log_loss
y_test_pred = model.predict(X_test)
y_test_proba = model.predict_proba(X_test)

test_accuracy = accuracy_score(y_test, y_test_pred)
test_f1 = f1_score(y_test, y_test_pred)
test_roc_auc = roc_auc_score(y_test, y_test_proba[:, 1])
test_logloss = log_loss(y_test, y_test_proba)

print(f"\nTest Set Performansı:")
print(f"  Accuracy: {test_accuracy:.4f}")
print(f"  F1 Score: {test_f1:.4f}")
print(f"  ROC-AUC: {test_roc_auc:.4f}")
print(f"  Log Loss: {test_logloss:.4f}")

print("\n" + "=" * 50)
print("MODEL KAYDETME")
print("=" * 50)

# Modeli kaydet
model_path = model_dir / 'lightgbm_model.pkl'
with open(model_path, 'wb') as f:
    pickle.dump(model, f)
print(f"✓ Model kaydedildi: {model_path}")

# Özellik sırasını kaydet (Unity'den gelen veriyi doğru sıraya koymak için)
feature_columns = list(X.columns)
features_path = model_dir / 'feature_columns.json'
with open(features_path, 'w', encoding='utf-8') as f:
    json.dump(feature_columns, f, indent=2, ensure_ascii=False)
print(f"✓ Özellik listesi kaydedildi: {features_path}")

# Özellik sayısını ve isimlerini göster
print(f"\n✓ Toplam özellik sayısı: {len(feature_columns)}")
print(f"\nÖzellik listesi:")
for i, feat in enumerate(feature_columns, 1):
    print(f"  {i:2d}. {feat}")

print("\n" + "=" * 50)
print("KAYIT TAMAMLANDI!")
print("=" * 50)
print(f"\nModel dosyası: {model_path}")
print(f"Özellik listesi: {features_path}")
print("\nWebSocket sunucusunu başlatmak için: python websocket_server.py")

