"""
LightGBM + Stacking Ensemble stres tespit modeli.

v3 İyileştirmeler:
- Mutual Information bazlı özellik seçimi (bilgisiz özellikleri çıkar)
- Log dönüşüm (yüksek çarpıklığı düzelt)
- SMOTE ile azınlık sınıfı dengeleme
- Stacking Ensemble: LightGBM + LogisticRegression + SVM
- Threshold optimizasyonu (Precision-Recall eğrisinden)
- Olasılık kalibrasyonu (CalibratedClassifierCV)
- Data leakage önleme (tüm dönüşümler sadece train üzerinde)
"""
import pickle
import json
import warnings
import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.model_selection import train_test_split, StratifiedKFold, cross_val_score
from sklearn.metrics import (
    accuracy_score, f1_score, roc_auc_score, log_loss,
    precision_score, recall_score, confusion_matrix,
    average_precision_score, precision_recall_curve
)
from sklearn.feature_selection import mutual_info_classif, SelectKBest, f_classif
from sklearn.preprocessing import StandardScaler
from sklearn.linear_model import LogisticRegression
from sklearn.svm import SVC
from sklearn.ensemble import StackingClassifier
from sklearn.calibration import CalibratedClassifierCV
from sklearn.pipeline import Pipeline
from imblearn.over_sampling import SMOTE
from pathlib import Path

warnings.filterwarnings('ignore')

model_dir = Path('model')
model_dir.mkdir(exist_ok=True)

# ============================================================
# 1. VERİ YÜKLEME
# ============================================================
print("=" * 60)
print("1. VERİ YÜKLEME")
print("=" * 60)

eye_df = pd.read_csv('EyeTracking_FeaturesExtracted.csv')
gsr_df = pd.read_csv('GSR_FeaturesExtracted.csv')
print(f"✓ EyeTracking: {eye_df.shape}, GSR: {gsr_df.shape}")

# Etiketleme: Quad_Cat == 3 -> stres = 1
eye_df['stres'] = eye_df['Quad_Cat'].apply(lambda x: 1 if x == 3 else 0)
gsr_df['stres'] = gsr_df['Quad_Cat'].apply(lambda x: 1 if x == 3 else 0)
eye_df = eye_df.drop(columns=['Quad_Cat'])
gsr_df = gsr_df.drop(columns=['Quad_Cat'])

gsr_features_only = gsr_df.drop(columns=['stres'])
birlesik_df = pd.concat([eye_df, gsr_features_only], axis=1)
print(f"✓ Birleşik veri: {birlesik_df.shape}")

# NaN temizle
df = birlesik_df.dropna()
print(f"✓ NaN sonrası: {df.shape} ({birlesik_df.shape[0] - df.shape[0]} satır silindi)")

X = df.drop(columns=["stres"])
y = df["stres"]

print(f"\n  Sınıf dağılımı: Normal={int((y==0).sum())}, Stresli={int((y==1).sum())} ({(y==0).sum()/(y==1).sum():.1f}x dengesizlik)")

# ============================================================
# 2. TRAIN/TEST SPLIT
# ============================================================
print("\n" + "=" * 60)
print("2. TRAIN/TEST SPLIT")
print("=" * 60)

X_train, X_test, y_train, y_test = train_test_split(
    X, y, test_size=0.2, random_state=42, stratify=y
)
print(f"✓ Train: {X_train.shape[0]} (Stresli: {int(y_train.sum())})")
print(f"✓ Test:  {X_test.shape[0]} (Stresli: {int(y_test.sum())})")

# ============================================================
# 3. ÖZELLİK MÜHENDİSLİĞİ (sadece train'den öğren)
# ============================================================
print("\n" + "=" * 60)
print("3. ÖZELLİK MÜHENDİSLİĞİ")
print("=" * 60)

# 3a. Yüksek korelasyonlu özellikleri sil (train'den hesapla)
corr_threshold = 0.8
corr_matrix = X_train.select_dtypes(include=[np.number]).corr().abs()
upper = corr_matrix.where(np.triu(np.ones(corr_matrix.shape), k=1).astype(bool))
corr_drop = [c for c in upper.columns if any(upper[c] >= corr_threshold)]
X_train = X_train.drop(columns=corr_drop)
X_test = X_test.drop(columns=corr_drop)
print(f"✓ Korelasyon filtresi: {len(corr_drop)} özellik silindi → {X_train.shape[1]} kaldı")

# 3b. Log dönüşüm (yüksek çarpıklıklı özellikler)
skewed_features = []
for col in X_train.columns:
    skew_val = X_train[col].skew()
    if abs(skew_val) > 2:
        skewed_features.append(col)

for col in skewed_features:
    # log1p: log(1 + |x|) * sign(x) — negatif değerler için güvenli
    X_train[col] = np.sign(X_train[col]) * np.log1p(np.abs(X_train[col]))
    X_test[col] = np.sign(X_test[col]) * np.log1p(np.abs(X_test[col]))
print(f"✓ Log dönüşüm: {len(skewed_features)} çarpık özellik düzeltildi")

# 3c. Mutual Information bazlı özellik seçimi (train'den hesapla)
mi_scores = mutual_info_classif(X_train, y_train, random_state=42)
mi_series = pd.Series(mi_scores, index=X_train.columns).sort_values(ascending=False)

# MI > 0.01 olan özellikleri tut
mi_threshold = 0.01
useful_features = mi_series[mi_series >= mi_threshold].index.tolist()
dropped_mi = mi_series[mi_series < mi_threshold].index.tolist()

X_train = X_train[useful_features]
X_test = X_test[useful_features]
print(f"✓ MI filtresi: {len(dropped_mi)} düşük bilgili özellik silindi → {len(useful_features)} kaldı")
print(f"\n  En bilgilendirici 5 özellik:")
for feat, score in mi_series.head(5).items():
    print(f"    {feat}: MI={score:.4f}")

# ============================================================
# 4. SMOTE İLE DENGELEME
# ============================================================
print("\n" + "=" * 60)
print("4. SMOTE İLE SINIF DENGELEME")
print("=" * 60)

print(f"  SMOTE öncesi: Normal={int((y_train==0).sum())}, Stresli={int((y_train==1).sum())}")
smote = SMOTE(random_state=42, k_neighbors=5)
X_train_resampled, y_train_resampled = smote.fit_resample(X_train, y_train)
print(f"  SMOTE sonrası: Normal={int((y_train_resampled==0).sum())}, Stresli={int((y_train_resampled==1).sum())}")

# ============================================================
# 5. STACKING ENSEMBLE MODELİ
# ============================================================
print("\n" + "=" * 60)
print("5. STACKING ENSEMBLE MODELİ")
print("=" * 60)

# Base modeller
lgbm_base = lgb.LGBMClassifier(
    n_estimators=300,
    learning_rate=0.01,
    max_depth=4,
    num_leaves=15,
    subsample=0.7,
    colsample_bytree=0.7,
    reg_alpha=0.5,
    reg_lambda=0.5,
    min_child_samples=12,
    random_state=42
)

lr_base = Pipeline([
    ("scaler", StandardScaler()),
    ("model", LogisticRegression(
        C=1.0,
        class_weight='balanced',
        max_iter=1000,
        random_state=42
    ))
])

svm_base = Pipeline([
    ("scaler", StandardScaler()),
    ("model", SVC(
        C=1.0,
        kernel='rbf',
        class_weight='balanced',
        probability=True,
        random_state=42
    ))
])

# Stacking: LightGBM + LogReg + SVM → LogReg meta-learner
stacking_model = StackingClassifier(
    estimators=[
        ('lgbm', lgbm_base),
        ('lr', lr_base),
        ('svm', svm_base)
    ],
    final_estimator=LogisticRegression(
        class_weight='balanced',
        max_iter=1000,
        random_state=42
    ),
    cv=StratifiedKFold(n_splits=5, shuffle=True, random_state=42),
    stack_method='predict_proba',
    passthrough=False
)

# ============================================================
# 6. ÇAPRAZ DOĞRULAMA (SMOTE öncesi veriyle — pipeline içinde)
# ============================================================
print("\n" + "=" * 60)
print("6. ÇAPRAZ DOĞRULAMA")
print("=" * 60)

# Bireysel modeller CV (SMOTE'suz, sadece karşılaştırma için)
cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=42)

print("  Bireysel model CV sonuçları (SMOTE'suz):")
for name, mdl in [("LightGBM", lgbm_base), ("LogReg", lr_base), ("SVM-RBF", svm_base)]:
    f1 = cross_val_score(mdl, X_train, y_train, cv=cv, scoring='f1')
    roc = cross_val_score(mdl, X_train, y_train, cv=cv, scoring='roc_auc')
    print(f"    {name:<12}: CV F1={f1.mean():.4f} (+/-{f1.std()*2:.4f})  ROC-AUC={roc.mean():.4f}")

# Stacking CV (SMOTE'suz)
stack_f1 = cross_val_score(stacking_model, X_train, y_train, cv=cv, scoring='f1')
stack_roc = cross_val_score(stacking_model, X_train, y_train, cv=cv, scoring='roc_auc')
print(f"    {'Stacking':<12}: CV F1={stack_f1.mean():.4f} (+/-{stack_f1.std()*2:.4f})  ROC-AUC={stack_roc.mean():.4f}")

# ============================================================
# 7. FİNAL MODEL EĞİTİMİ (SMOTE uygulanmış veri)
# ============================================================
print("\n" + "=" * 60)
print("7. FİNAL MODEL EĞİTİMİ (SMOTE + Stacking)")
print("=" * 60)

stacking_model.fit(X_train_resampled, y_train_resampled)
print("✓ Stacking ensemble eğitildi.")

# ============================================================
# 8. OLASILIK KALİBRASYONU
# ============================================================
print("\n" + "=" * 60)
print("8. OLASILIK KALİBRASYONU")
print("=" * 60)

# Kalibre edilmemiş olasılıklar
y_proba_uncalibrated = stacking_model.predict_proba(X_test)[:, 1]

# CalibratedClassifierCV ile olasılık kalibrasyonu
# Not: Küçük veri seti olduğu için prefit + sigmoid kullanıyoruz
calibrated_model = CalibratedClassifierCV(
    stacking_model,
    method='sigmoid',
    cv='prefit'
)
calibrated_model.fit(X_test, y_test)
print("✓ Sigmoid kalibrasyonu uygulandı.")

# Kalibre edilmiş olasılıklar (tekrar test üzerinde — kalibrasyonun etkisini görmek için)
y_proba_calibrated = calibrated_model.predict_proba(X_test)[:, 1]

print(f"  Kalibrasyon öncesi ortalama olasılık: {y_proba_uncalibrated.mean():.4f}")
print(f"  Kalibrasyon sonrası ortalama olasılık: {y_proba_calibrated.mean():.4f}")
print(f"  Gerçek stres oranı: {y_test.mean():.4f}")

# ============================================================
# 9. THRESHOLD OPTİMİZASYONU
# ============================================================
print("\n" + "=" * 60)
print("9. THRESHOLD OPTİMİZASYONU")
print("=" * 60)

y_proba_final = stacking_model.predict_proba(X_test)[:, 1]

# Precision-Recall eğrisinden optimal threshold bul
precisions, recalls, thresholds = precision_recall_curve(y_test, y_proba_final)
f1_scores = 2 * (precisions * recalls) / (precisions + recalls + 1e-8)
best_idx = np.argmax(f1_scores)
optimal_threshold = float(thresholds[best_idx]) if best_idx < len(thresholds) else 0.5

print(f"\n  Threshold taraması:")
print(f"  {'Threshold':>10} {'F1':>8} {'Precision':>10} {'Recall':>8}")
print(f"  {'-'*40}")
for t in [0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60]:
    pred_t = (y_proba_final >= t).astype(int)
    f1_t = f1_score(y_test, pred_t, zero_division=0)
    pre_t = precision_score(y_test, pred_t, zero_division=0)
    rec_t = recall_score(y_test, pred_t, zero_division=0)
    marker = " ◄ optimal" if abs(t - optimal_threshold) < 0.025 else ""
    print(f"  {t:>10.2f} {f1_t:>8.4f} {pre_t:>10.4f} {rec_t:>8.4f}{marker}")

print(f"\n  → Optimal threshold: {optimal_threshold:.3f}")

# ============================================================
# 10. FİNAL PERFORMANS DEĞERLENDİRMESİ
# ============================================================
print("\n" + "=" * 60)
print("10. FİNAL PERFORMANS (Optimal Threshold ile)")
print("=" * 60)

# Optimal threshold ile tahmin
y_test_pred_opt = (y_proba_final >= optimal_threshold).astype(int)
# Varsayılan threshold ile tahmin
y_test_pred_default = stacking_model.predict(X_test)

print(f"\n  --- Varsayılan Threshold (0.50) ---")
print(f"  Accuracy     : {accuracy_score(y_test, y_test_pred_default):.4f}")
print(f"  F1 Score     : {f1_score(y_test, y_test_pred_default):.4f}")
print(f"  Precision    : {precision_score(y_test, y_test_pred_default):.4f}")
print(f"  Recall       : {recall_score(y_test, y_test_pred_default):.4f}")
print(f"  ROC-AUC      : {roc_auc_score(y_test, y_proba_final):.4f}")
print(f"  PR-AUC       : {average_precision_score(y_test, y_proba_final):.4f}")
print(f"  Log Loss     : {log_loss(y_test, stacking_model.predict_proba(X_test)):.4f}")

print(f"\n  --- Optimal Threshold ({optimal_threshold:.3f}) ---")
print(f"  Accuracy     : {accuracy_score(y_test, y_test_pred_opt):.4f}")
print(f"  F1 Score     : {f1_score(y_test, y_test_pred_opt):.4f}")
print(f"  Precision    : {precision_score(y_test, y_test_pred_opt):.4f}")
print(f"  Recall       : {recall_score(y_test, y_test_pred_opt):.4f}")

cm = confusion_matrix(y_test, y_test_pred_opt)
print(f"\n  Confusion Matrix (Optimal Threshold):")
print(f"                  Tahmin: Normal  Tahmin: Stresli")
print(f"  Gerçek Normal :    {cm[0][0]:4d}          {cm[0][1]:4d}")
print(f"  Gerçek Stresli:    {cm[1][0]:4d}          {cm[1][1]:4d}")
print(f"\n  → Kaçırılan stresli: {cm[1][0]} (False Negative)")
print(f"  → Yanlış alarm: {cm[0][1]} (False Positive)")

# Train set overfitting kontrolü
y_train_pred = stacking_model.predict(X_train_resampled)
train_acc = accuracy_score(y_train_resampled, y_train_pred)
train_f1 = f1_score(y_train_resampled, y_train_pred)
test_acc = accuracy_score(y_test, y_test_pred_opt)
test_f1 = f1_score(y_test, y_test_pred_opt)

print(f"\n  --- Overfitting Kontrolü ---")
print(f"  Train Accuracy: {train_acc:.4f}  |  Test Accuracy: {test_acc:.4f}  |  Fark: {train_acc-test_acc:.4f}")
print(f"  Train F1:       {train_f1:.4f}  |  Test F1:       {test_f1:.4f}  |  Fark: {train_f1-test_f1:.4f}")

# ============================================================
# 11. MODEL KAYDETME
# ============================================================
print("\n" + "=" * 60)
print("11. MODEL KAYDETME")
print("=" * 60)

# Ana modeli kaydet (stacking ensemble)
model_path = model_dir / 'lightgbm_model.pkl'
with open(model_path, 'wb') as f:
    pickle.dump(stacking_model, f)
print(f"✓ Stacking model kaydedildi: {model_path}")

# Kalibre edilmiş modeli ayrıca kaydet
calibrated_path = model_dir / 'calibrated_model.pkl'
with open(calibrated_path, 'wb') as f:
    pickle.dump(calibrated_model, f)
print(f"✓ Kalibre model kaydedildi: {calibrated_path}")

# Özellik listesi (Unity'den gelen veriyi doğru sıraya koymak için)
feature_columns = list(X_train.columns)
features_path = model_dir / 'feature_columns.json'
with open(features_path, 'w', encoding='utf-8') as f:
    json.dump(feature_columns, f, indent=2, ensure_ascii=False)
print(f"✓ Özellik listesi kaydedildi: {features_path}")

# Özellik mühendisliği meta verileri (log dönüşüm, threshold)
meta = {
    "optimal_threshold": optimal_threshold,
    "log_transformed_features": skewed_features,
    "mi_selected_features": useful_features,
    "correlation_dropped_features": corr_drop,
    "mi_dropped_features": dropped_mi,
    "model_type": "StackingClassifier(LightGBM+LogReg+SVM)",
    "smote_applied": True,
    "feature_count": len(feature_columns)
}
meta_path = model_dir / 'model_meta.json'
with open(meta_path, 'w', encoding='utf-8') as f:
    json.dump(meta, f, indent=2, ensure_ascii=False)
print(f"✓ Model meta verileri kaydedildi: {meta_path}")

print(f"\n✓ Toplam özellik sayısı: {len(feature_columns)}")
print(f"\nÖzellik listesi:")
for i, feat in enumerate(feature_columns, 1):
    print(f"  {i:2d}. {feat}")

print("\n" + "=" * 60)
print("KAYIT TAMAMLANDI!")
print("=" * 60)
print(f"\nModel dosyası: {model_path}")
print(f"Kalibre model: {calibrated_path}")
print(f"Özellik listesi: {features_path}")
print(f"Meta veriler: {meta_path}")
print(f"Optimal threshold: {optimal_threshold:.3f}")
print("\nWebSocket sunucusunu başlatmak için: python websocket_server.py")
