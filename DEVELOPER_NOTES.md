# Servis Hakediş — Geliştirici Notları

> Bu dosya özel GitLab reposunda saklanır. Kimseye verilmez.

---

## Lisans Sistemi

### Algoritma
- **ECDSA P-256** (IEEE P1363 — raw 64-byte r||s imzası)
- Payload: 8 byte → `[machineId 6B] + [expiryDays 2B (UInt16 LE, epoch: 2025-01-01)]`
- Encoding: 75 byte → Base32 RFC 4648 → 120 char → 24×5 grup (tire ayrımlı) = **143 karakter**

### ECDSA P-256 Anahtarları

**Private Key (PKCS8, Base64) — SADECE LicenseGenerator'da kullanılır:**
```
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgTOIW8wHp+NHKxHuYBp1wJvXey99MrpiUu7B2p3JLfXyhRANCAAQcGKKiiTK5a07KeDSPpLF3LyuPweuGWV3L43MIh2CXaIVS/w792JYTg9ER7SAvctyHu7NpbjWkK6UzWesQgU6x
```

**Public Key (SubjectPublicKeyInfo, Base64) — uygulamaya gömülü:**
```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHBiiookyuWtOyng0j6Sxdy8rj8Hrhlldy+NzCIdgl2iFUv8O/diWE4PREe0gL3Lch7uzaW41pCulM1nrEIFOsQ==
```

Private key dosyası: `HakedisOtomasyon/tools/LicenseGenerator/private_key.b64`

---

## Geliştirici PC — Kalıcı Lisans (2099)

**MachineId (SHA-256 hex, 64 char):**
```
c1cfaf944dd27c9c992e0473c117c668e37ecf5c9b3374129c843412b11787f9
```

**Kısa cihaz kodu:** `SMK-C1CF-AF94-4DD2`

**Aktivasyon Kodu (bitiş: 31.12.2099):**
```
YHH27-FCN2I-AGXQI-OZKSA-V6H3Z-ZEO6E-DT6AN-53R5O-2OIHX-USDW6-ZTAHW-4VRBE-NYWD5-OKQ4F-5GUZ3-Q2RU2-ILHIR-FHGR7-FSOXB-4QEY5-Y7FKR-BBAU2-C3MO2-QAAAA
```

Firma adı: `ADMIN`

**Lisans dosyası konumu (runtime):** `C:\ProgramData\ServisHakedis\license.dat`

PC formatlandıktan sonra yeni kurulumda:
1. Uygulamayı aç → aktivasyon penceresi çıkar
2. Cihaz kodu `SMK-C1CF-AF94-4DD2` ise → yukarıdaki aktivasyon kodunu gir
3. Cihaz kodu farklıysa → `LicenseGenerator.exe` ile yeni kod üret (aşağıya bak)

---

## Yeni Lisans Üretimi (Müşteri için)

1. `HakedisOtomasyon/tools/LicenseGenerator/` klasörüne git
2. `dotnet run` veya derlenmiş exe'yi çalıştır
3. `private_key.b64` aynı klasörde olmalı (repoda mevcut)
4. Generator'a gir:
   - Firma adı (müşteri firma adı)
   - Cihaz kodu (müşterinin SMK-XXXX-XXXX-XXXX kodu)
   - Bitiş tarihi (gg.aa.yyyy)
5. Üretilen 143 karakterlik kodu müşteriye ilet

---

## MachineId Hesabı

```
SHA-256( MachineGuid + "|" + ComputerName )
```
- `MachineGuid`: `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`
- `ComputerName`: `Environment.MachineName`
- UserName **dahil değil** → aynı PC'de farklı Windows kullanıcıları aynı lisansı kullanır

---

## Lisans Dosyası

- Konum: `%ProgramData%\ServisHakedis\license.dat` (machine-wide)
- Format: `base64(json).hmac_sha256_hex`
- HMAC anahtarı: `SHA-256( MachineId + "#ServisHakedisLicenseFile" )` — makineye özgü, kopyalanamaz

---

## Önemli Dosyalar

| Dosya | Açıklama |
|-------|----------|
| `HakedisOtomasyon/tools/LicenseGenerator/Program.cs` | Lisans üretici kaynak kodu |
| `HakedisOtomasyon/tools/LicenseGenerator/private_key.b64` | ECDSA private key |
| `HakedisOtomasyon/src/HakedisOtomasyon.Web/Licensing/ActivationCodeValidator.cs` | Aktivasyon kodu doğrulayıcı |
| `HakedisOtomasyon/src/HakedisOtomasyon.Web/Licensing/LicenseService.cs` | Lisans dosyası yönetimi |
| `HakedisOtomasyon/src/HakedisOtomasyon.Web/Licensing/MachineIdService.cs` | Cihaz kimliği üretici |
