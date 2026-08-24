using System.Security.Cryptography;
using System.Text;

namespace HakedisOtomasyon.Web.Licensing;

/// <summary>
/// Aktivasyon kodunu doğrular.
///
/// Format (toplam 143 karakter):
///   24 adet 5-char grup, tire ile ayrılmış
///   Örnek: ABCDE-FGHIJ-KLMNO-...
///
/// Yapı (75 byte → 120 Base32 char):
///   [0..5]  machineId ilk 6 byte (12 hex char)
///   [6..7]  2025-01-01'den gün sayısı (UInt16 LE)
///   [8..71] ECDSA P-256 imzası (64 byte, IEEE P1363: r||s)
///   [72..74] sıfır padding (Base32 gruplama için)
/// </summary>
public static class ActivationCodeValidator
{
    // ECDSA P-256 public key — SubjectPublicKeyInfo, Base64
    // Private key yalnızca generatörde saklanır.
    private const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHBiiookyuWtOyng0j6Sxdy8rj8Hr" +
        "hlldy+NzCIdgl2iFUv8O/diWE4PREe0gL3Lch7uzaW41pCulM1nrEIFOsQ==";

    private static readonly DateTime EpochDate =
        new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // RFC 4648 Base32 alfabesi
    private const string B32Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Aktivasyon kodunu doğrular.
    /// Firma adı artık kod içinde yer almaz; sadece lisans dosyasına kaydedilir.
    /// </summary>
    public static (bool IsValid, string? Error, LicenseInfo? License) Validate(
        string activationCode,
        string currentMachineId,
        string firmaAdi)
    {
        if (string.IsNullOrWhiteSpace(activationCode))
            return (false, "Aktivasyon kodu boş olamaz.", null);

        if (string.IsNullOrWhiteSpace(firmaAdi))
            return (false, "Firma adı boş olamaz.", null);

        // Tire, boşluk, satır sonu temizle
        var clean = activationCode
            .Replace("-", "").Replace(" ", "")
            .Replace("\n", "").Replace("\r", "")
            .ToUpperInvariant().Trim();

        byte[] raw;
        try { raw = Base32Decode(clean); }
        catch { return (false, "Geçersiz aktivasyon kodu formatı.", null); }

        if (raw.Length < 72)
            return (false, "Aktivasyon kodu eksik veya hatalı.", null);

        var payload   = raw[..8];    // 6 byte machineId + 2 byte expiryDays
        var signature = raw[8..72];  // 64 byte ECDSA imzası

        // 1. ECDSA P-256 imza doğrulaması
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(PublicKeyBase64), out _);

            if (!ecdsa.VerifyData(payload, signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return (false, "Aktivasyon kodu imzası geçersiz.", null);
        }
        catch
        {
            return (false, "İmza doğrulaması sırasında hata oluştu.", null);
        }

        // 2. MachineId doğrulama
        var machineIdHex12 = currentMachineId.Length >= 12
            ? currentMachineId[..12] : currentMachineId;

        byte[] currentIdBytes;
        try { currentIdBytes = Convert.FromHexString(machineIdHex12); }
        catch { return (false, "Cihaz kimliği okunamadı.", null); }

        if (!payload[..6].SequenceEqual(currentIdBytes))
            return (false, "Bu aktivasyon kodu bu bilgisayara ait değil.", null);

        // 3. Tarih doğrulama
        var expiryDays = BitConverter.ToUInt16(payload, 6);
        var expiryDate = EpochDate.AddDays(expiryDays);

        if (expiryDate < DateTime.UtcNow)
            return (false, $"Lisans süresi dolmuştur ({expiryDate:dd.MM.yyyy}).", null);

        return (true, null, new LicenseInfo(machineIdHex12, firmaAdi.Trim(), expiryDate));
    }

    // ──────────────────────────────────────────────────────────────────
    // Base32 yardımcıları (RFC 4648, padding yok)
    // ──────────────────────────────────────────────────────────────────

    public static byte[] Base32Decode(string s)
    {
        s = s.TrimEnd('=');
        var bits = 0;
        var bitBuffer = 0;
        var output = new List<byte>();

        foreach (var c in s)
        {
            var val = B32Chars.IndexOf(c);
            if (val < 0) throw new FormatException($"Geçersiz Base32 karakter: {c}");
            bitBuffer = (bitBuffer << 5) | val;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(bitBuffer >> bits));
                bitBuffer &= (1 << bits) - 1;
            }
        }
        return [.. output];
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        var bits = 0;
        var bitBuffer = 0;

        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(B32Chars[(bitBuffer >> bits) & 0x1F]);
            }
        }
        if (bits > 0)
            sb.Append(B32Chars[(bitBuffer << (5 - bits)) & 0x1F]);

        return sb.ToString();
    }

    /// <summary>
    /// 8 byte payload + 64 byte imza → 143 karakterlik gruplu aktivasyon kodu.
    /// (Generatörden çağrılır; app içinde doğrulama-only)
    /// </summary>
    public static string FormatCode(byte[] payload8, byte[] signature64)
    {
        // 75 byte: 8 payload + 64 sig + 3 sıfır padding → tam 120 Base32 char (24×5)
        var raw = new byte[75];
        Buffer.BlockCopy(payload8,    0, raw, 0,  8);
        Buffer.BlockCopy(signature64, 0, raw, 8, 64);

        var b32 = Base32Encode(raw).PadRight(120, 'A');

        var sb = new StringBuilder();
        for (var i = 0; i < 120; i += 5)
        {
            if (i > 0) sb.Append('-');
            sb.Append(b32, i, 5);
        }
        return sb.ToString();
    }
}

/// <summary>Doğrulanmış lisans bilgisi</summary>
public record LicenseInfo(string MachineId, string FirmaAdi, DateTime ExpiryDate);
