using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HakedisOtomasyon.Application;

namespace HakedisOtomasyon.Web;

/// <summary>
/// Servis formundaki el yazısı mağaza kodunu/adını, servis tarihinin gün/ayını ve "Kullanılan Malzeme" tablosundaki gaz / glikol
/// miktarını Google Gemini ile okur (form numarası bu sınıfın işi değildir — Windows OCR). Model ve anahtar Ayarlar ekranından
/// seçilir (<see cref="AiSettings"/>, .env: GEMINI_MODEL, GEMINI_API_KEY); her okumada yeniden okunur, kaydedilince hemen geçerlidir.
/// Gönderilen görsel Azure'unkiyle aynıdır: formun üst kısmı + personel/malzeme tabloları + alt TARİH şeridi (açık sayfa).
/// </summary>
public sealed class GeminiHandwrittenStoreReader : IHandwrittenStoreReader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    private const string Prompt = """
        Bu görsel, Migros mağazalarına yapılan soğutma servisinin elle doldurulmuş servis formudur: formun üst kısmı, ortadaki
        personel ve "KULLANILAN MALZEME" tabloları ve en alttaki TARİH satırı alt alta birleştirilmiştir. EL YAZISI bilgileri oku:
        - store_code: Migros mağaza kodu (3-5 haneli sayı). "ÇAĞRI BİLDİREN" / "İŞİN YERİ" satırında mağaza adının yanında
          (parantezli ya da parantezsiz, ör. "GOP MM MİGROS (383)", "Zirve Kent 3M Migros 1996") ya da formun üst kısmına elle
          yazılmış olur. Basılı form/servis numarasını (sağ üstteki kırmızı "21144"), tarihleri ve telefon/vergi numaralarını
          mağaza kodu SANMA. Yoksa null.
        - store_name: elle yazılmış mağaza adı, yazıldığı gibi. Yoksa null.
        - form_day, form_month: servis tarihinin GÜNÜ ve AYI (tam sayı); en alttaki "TARİH" satırından, orası boşsa personel
          tablosundaki "Tarih" sütunundan. Biçim gün.ay'dır ("20.4.26" → 20 ve 4). YILI OKUMA. Emin değilsen null.
        - gas_quantity: "KULLANILAN MALZEME" tablosunda soğutucu gaz satırının (ör. "R404 A Gaz", "R-22", yalnız "gaz")
          "Miktarı" sütunundaki sayı, kg olarak ("25kg" → 25, "5,5" → 5.5). Gaz satırı yoksa null.
        - gas_type: o satırda yazan gaz türü ("R404", "R22", "R407C", "R410A", "R134A"...). Yoksa null.
        - glycol_quantity: aynı tablodaki glikol / antifriz satırının miktarı (kg). Yoksa null.
        - quantities_certain: miktar(lar)ı net okuyabildiysen true, yazı silik/karışık ise false.
        Emin olmadığın alanı null bırak. Yalnızca JSON döndür:
        {"store_code": ..., "store_name": ..., "form_day": ..., "form_month": ..., "gas_quantity": ..., "gas_type": ...,
         "glycol_quantity": ..., "quantities_certain": ...}
        """;

    public bool IsConfigured => AiSettings.GeminiConfigured;

    public async Task<HandwrittenStoreResult> ReadAsync(string absoluteFilePath, int pageIndex = 0, CancellationToken ct = default)
    {
        var key = AiSettings.Get(AiSettings.GeminiKeyVar);
        var model = AiSettings.GeminiModel;
        if (key is null) return new(null, null, "Gemini anahtarı girilmemiş (Ayarlar → Yapay Zekâ); mağaza elle seçilmeli.");
        if (!File.Exists(absoluteFilePath)) return new(null, null, "Servis formu dosyası bulunamadı.");

        byte[]? jpeg;
        try { jpeg = await Task.Run(() => AzureHandwrittenStoreReader.PageJpeg(absoluteFilePath, pageIndex), ct); }
        catch (Exception ex) { return new(null, null, $"Sayfa görüntüye çevrilemedi: {ex.Message}"); }
        if (jpeg is null) return new(null, null, "Sayfa görüntüye çevrilemedi.");

        try
        {
            var body = new
            {
                contents = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = Prompt },
                            new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(jpeg) } },
                        },
                    },
                },
                generationConfig = new { temperature = 0, responseMimeType = "application/json" },
            };
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(key)}";
            using var response = await Http.PostAsync(url, content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new(null, null, (int)response.StatusCode switch
                {
                    429 => "Gemini istek sınırı doldu; biraz sonra tekrar deneyin.",
                    400 or 401 or 403 => "Gemini anahtarı geçersiz (Ayarlar → Yapay Zekâ).",
                    404 => $"Gemini modeli bulunamadı: {model} (Ayarlar → Yapay Zekâ).",
                    _ => $"Gemini yanıt vermedi (HTTP {(int)response.StatusCode}).",
                });

            using var doc = JsonDocument.Parse(text);
            var output = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(output)) return new(null, null, "Gemini boş yanıt döndürdü.");

            using var result = JsonDocument.Parse(output);
            var root = result.RootElement.ValueKind == JsonValueKind.Array && result.RootElement.GetArrayLength() > 0
                ? result.RootElement[0] : result.RootElement;
            var certain = root.TryGetProperty("quantities_certain", out var c) && c.ValueKind == JsonValueKind.True;
            return new(Clean(Get(root, "store_code")), Clean(Get(root, "store_name")), null)
            {
                Day = Int(Get(root, "form_day")),
                Month = Int(Get(root, "form_month")),
                Gas = Reading(Get(root, "gas_quantity"), Clean(Get(root, "gas_type")), certain),
                Glycol = Reading(Get(root, "glycol_quantity"), null, certain),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, null, "Gemini zamanında yanıt vermedi.");
        }
        catch (HttpRequestException)
        {
            return new(null, null, "Gemini'ye bağlanılamadı (internet / ağ engeli).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, null, $"Gemini yanıtı okunamadı: {ex.Message}");
        }
    }

    /// <summary>Miktar okunduysa; "kesin değil" dendiyse ya da değer akla yatkın değilse teyit istenir.</summary>
    private static GasReading? Reading(string? raw, string? type, bool certain)
    {
        raw = Clean(raw);
        if (raw is null) return null;
        decimal? qty = decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var q) && q > 0 ? q : null;
        var sure = certain && qty is > 0 and <= 200;
        return new(qty, raw, type?.Replace("-", string.Empty).ToUpperInvariant(), !sure, sure ? null : $"Miktar net okunamadı (\"{raw}\").");
    }

    private static string? Get(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static int? Int(string? s) => int.TryParse(Clean(s), out var n) ? n : null;

    private static string? Clean(string? s)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }
}

/// <summary>Ayarlarda seçili sağlayıcıya (Gemini / Azure) yönlendiren okuyucu; seçim her okumada yeniden okunur.</summary>
public sealed class SelectedStoreReader : IHandwrittenStoreReader
{
    private readonly GeminiHandwrittenStoreReader _gemini = new();
    private readonly AzureHandwrittenStoreReader _azure = new();

    private IHandwrittenStoreReader Current => AiSettings.Provider == AiSettings.Azure ? _azure : _gemini;

    public bool IsConfigured => Current.IsConfigured;

    public Task<HandwrittenStoreResult> ReadAsync(string absoluteFilePath, int pageIndex = 0, CancellationToken cancellationToken = default)
        => Current.ReadAsync(absoluteFilePath, pageIndex, cancellationToken);
}
