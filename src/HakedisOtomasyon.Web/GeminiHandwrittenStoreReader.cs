using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HakedisOtomasyon.Application;
using SkiaSharp;

namespace HakedisOtomasyon.Web;

/// <summary>
/// Servis formuna elle yazılmış mağaza kodunu/adını Google Gemini (Flash Lite) ile okur. Tarih okunmaz.
/// Form numarası bu sınıfın işi değildir (Windows OCR — <see cref="WindowsFormNumberReader"/>).
///   • Yalnız istenen sayfa (çok sayfalı PDF'de o sayfa; maliyet tek sayfalık formla aynı) 150 DPI çizilir, üst %55'i kesilir (mağaza bilgisi formun başında) ve en fazla 1400 px genişliğe
///     küçültülüp JPEG olarak gönderilir — büyük görsellerde yaşanan zaman aşımları önlenir.
///   • API anahtarı GEMINI_API_KEY ortam değişkeninden (.env) okunur; hiçbir yere yazılmaz/loglanmaz.
/// </summary>
public sealed class GeminiHandwrittenStoreReader : IHandwrittenStoreReader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    private const string Prompt = """
        Bu görsel, Migros mağazalarına yapılan soğutma servisinin elle doldurulmuş servis formunun üst kısmıdır.
        Formdaki EL YAZISI mağaza bilgisini oku:
        - store_code: Migros mağaza kodu (3-5 haneli sayı). Genelde mağaza adının yanında parantez içinde ("GOP MM MİGROS (383)")
          ya da formun üst köşesine elle yazılmış olur ("6671", "(5159)"). Basılı form/servis numarasını (ör. kırmızı "17793",
          "005027"), tarihleri ve telefon/vergi numaralarını mağaza kodu SANMA. Yoksa null.
        - store_name: elle yazılmış mağaza adı, yazıldığı gibi (ör. "GOP MM MİGROS", "Kastamall AVM Migros"). Yoksa null.
        Emin olmadığın alanı null bırak. Yalnızca JSON döndür: {"store_code": ..., "store_name": ...}
        """;

    private readonly string? _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    private readonly string _model = Environment.GetEnvironmentVariable("GEMINI_MODEL") is { Length: > 0 } m ? m : "gemini-3.5-flash-lite";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<HandwrittenStoreResult> ReadAsync(string absoluteFilePath, int pageIndex = 0, CancellationToken ct = default)
    {
        if (!IsConfigured) return new(null, null, "Yapay zekâ anahtarı (GEMINI_API_KEY) tanımlı değil; mağaza elle seçilmeli.");
        if (!File.Exists(absoluteFilePath)) return new(null, null, "Servis formu dosyası bulunamadı.");

        byte[]? jpeg;
        try { jpeg = await Task.Run(() => PrepareImage(absoluteFilePath, pageIndex), ct); }
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
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
            using var response = await Http.PostAsync(url, content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new(null, null, (int)response.StatusCode == 429
                    ? "Yapay zekâ istek sınırı doldu; biraz sonra tekrar deneyin."
                    : $"Yapay zekâ yanıt vermedi (HTTP {(int)response.StatusCode}).");

            using var doc = JsonDocument.Parse(text);
            var output = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(output)) return new(null, null, "Yapay zekâ boş yanıt döndürdü.");

            using var result = JsonDocument.Parse(output);
            var root = result.RootElement.ValueKind == JsonValueKind.Array && result.RootElement.GetArrayLength() > 0
                ? result.RootElement[0] : result.RootElement;
            return new(Clean(Get(root, "store_code")), Clean(Get(root, "store_name")), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, null, "Yapay zekâ zamanında yanıt vermedi.");
        }
        catch (HttpRequestException)
        {
            return new(null, null, "Yapay zekâya bağlanılamadı (internet / ağ engeli).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, null, $"Yapay zekâ yanıtı okunamadı: {ex.Message}");
        }
    }

    private static string? Get(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static string? Clean(string? s)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    /// <summary>Tek sayfayı çizer, üst %55'ini keser, en fazla 1400 px genişliğe küçültüp JPEG'e çevirir.</summary>
    internal static byte[]? PrepareImage(string path, int pageIndex = 0)
    {
        using var page = ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())
            ? SKBitmap.Decode(path)
            : PDFtoImage.Conversion.ToImage(File.ReadAllBytes(path), page: pageIndex, options: new PDFtoImage.RenderOptions(Dpi: 150));
        if (page is null) return null;

        var rect = new SKRectI(0, 0, page.Width, (int)(page.Height * 0.55));
        using var crop = new SKBitmap(rect.Width, rect.Height);
        if (!page.ExtractSubset(crop, rect)) return null;

        var scale = Math.Min(1.0, 1400.0 / crop.Width);
        using var small = scale < 1
            ? crop.Resize(new SKImageInfo((int)(crop.Width * scale), (int)(crop.Height * scale)), SKFilterQuality.High)
            : crop.Copy();
        using var image = SKImage.FromBitmap(small);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
