using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HakedisOtomasyon.Web.Licensing;

/// <summary>
/// Lisans dosyasını yönetir.
///
/// Dosya konumu: %ProgramData%\ServisHakedis\license.dat
/// (Makine geneli — tüm Windows kullanıcıları paylaşır, tek aktivasyon yeterli)
///
/// HMAC anahtarı makineye özeldir: sabit string yerine MachineId'den türetilir.
/// Böylece başka bir makinede üretilen license.dat bu makinede çalışmaz.
/// </summary>
public static class LicenseService
{
    private static readonly string LicensePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ServisHakedis",
        "license.dat");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Lisans geçerli mi? Startup'ta çağrılır.
    /// </summary>
    public static bool IsLicenseValid()
    {
        var payload = TryLoadLicensePayload();
        if (payload is null) return false;

        var fullId       = MachineIdService.GetMachineId();
        var currentShort = fullId.Length >= 12 ? fullId[..12] : fullId;

        if (!string.Equals(payload.MachineId, currentShort,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (payload.ExpiryDate < DateTime.UtcNow)
            return false;

        return true;
    }

    /// <summary>
    /// Aktivasyon kodunu doğrulayıp lisans dosyasını kaydeder.
    /// </summary>
    public static (bool Success, string? Error) ActivateAndSave(
        string activationCode,
        string firmaAdi)
    {
        var machineId = MachineIdService.GetMachineId();
        var (isValid, error, license) = ActivationCodeValidator.Validate(
            activationCode, machineId, firmaAdi);

        if (!isValid || license is null)
            return (false, error);

        try
        {
            var dir = Path.GetDirectoryName(LicensePath)!;
            Directory.CreateDirectory(dir);

            var filePayload = new LicenseFilePayload(
                license.MachineId,
                license.FirmaAdi,
                license.ExpiryDate,
                activationCode.Trim(),
                DateTime.UtcNow);

            var json    = JsonSerializer.Serialize(filePayload, _jsonOpts);
            var jsonB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var hmac    = ComputeHmac(jsonB64);

            File.WriteAllText(LicensePath, $"{jsonB64}.{hmac}", Encoding.ASCII);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Lisans dosyası kaydedilemedi: {ex.Message}");
        }
    }

    /// <summary>
    /// Lisans bilgilerini döner (ekranda göstermek için). Lisans yoksa null.
    /// </summary>
    public static LicenseInfo? GetCurrentLicense()
    {
        var payload = TryLoadLicensePayload();
        if (payload is null) return null;
        return new LicenseInfo(payload.MachineId, payload.FirmaAdi, payload.ExpiryDate);
    }

    // ──────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// HMAC anahtarı MachineId'den türetilir — makineler arası lisans kopyalamayı engeller.
    /// </summary>
    private static byte[] GetFileHmacKey() =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            MachineIdService.GetMachineId() + "#ServisHakedisLicenseFile"));

    private static LicenseFilePayload? TryLoadLicensePayload()
    {
        try
        {
            if (!File.Exists(LicensePath)) return null;

            var content = File.ReadAllText(LicensePath, Encoding.ASCII).Trim();
            var dotIdx  = content.LastIndexOf('.');
            if (dotIdx < 0) return null;

            var payloadB64  = content[..dotIdx];
            var storedHmac  = content[(dotIdx + 1)..];
            var expectedHmac = ComputeHmac(payloadB64);

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(storedHmac),
                    Encoding.ASCII.GetBytes(expectedHmac)))
                return null;

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payloadB64));
            return JsonSerializer.Deserialize<LicenseFilePayload>(json, _jsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeHmac(string data)
    {
        var hash = HMACSHA256.HashData(GetFileHmacKey(), Encoding.ASCII.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private record LicenseFilePayload(
        [property: JsonPropertyName("machineId")]      string   MachineId,
        [property: JsonPropertyName("firmaAdi")]       string   FirmaAdi,
        [property: JsonPropertyName("expiryDate")]     DateTime ExpiryDate,
        [property: JsonPropertyName("activationCode")] string   ActivationCode,
        [property: JsonPropertyName("activatedAt")]    DateTime ActivatedAt);
}
