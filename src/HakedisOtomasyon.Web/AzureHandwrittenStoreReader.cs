using System.IO;
using System.Net.Http;
using System.Text.Json;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Infrastructure.PdfProcessing;
using SkiaSharp;

namespace HakedisOtomasyon.Web;

/// <summary>
/// Servis formundaki el yazısı mağaza kodunu/adını ve servis tarihinin gün/ayını Azure Document Intelligence (prebuilt-layout,
/// el yazısı destekli) ile okur. Gemini'nin yerine kullanılır (kullanıcı kararı, 2026-11). Form numarası bu sınıfın işi değildir.
///   • Açık sayfa (çok sayfalı PDF'de yalnız o sayfa) 150 DPI JPEG olarak gönderilir; WebP vb. Azure'un kabul etmediği
///     görseller de JPEG'e çevrilir.
///   • Adres ve anahtar .env'den: AZURE_DI_ENDPOINT, AZURE_DI_KEY (hiçbir yere yazılmaz/loglanmaz).
///   • Okunan satırlardan mağaza ve tarihi <see cref="AzureFormParser"/> çıkarır.
/// </summary>
public sealed class AzureHandwrittenStoreReader : IHandwrittenStoreReader
{
    private const string ApiVersion = "2024-11-30";
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    private readonly string? _endpoint = Environment.GetEnvironmentVariable("AZURE_DI_ENDPOINT")?.Trim().TrimEnd('/');
    private readonly string? _key = Environment.GetEnvironmentVariable("AZURE_DI_KEY")?.Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_endpoint) && !string.IsNullOrWhiteSpace(_key);

    public async Task<HandwrittenStoreResult> ReadAsync(string absoluteFilePath, int pageIndex = 0, CancellationToken ct = default)
    {
        if (!IsConfigured) return new(null, null, "Azure form okuma ayarlı değil (AZURE_DI_ENDPOINT / AZURE_DI_KEY); mağaza elle seçilmeli.");
        if (!File.Exists(absoluteFilePath)) return new(null, null, "Servis formu dosyası bulunamadı.");

        byte[]? jpeg;
        try { jpeg = await Task.Run(() => PageJpeg(absoluteFilePath, pageIndex), ct); }
        catch (Exception ex) { return new(null, null, $"Sayfa görüntüye çevrilemedi: {ex.Message}"); }
        if (jpeg is null) return new(null, null, "Sayfa görüntüye çevrilemedi.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_endpoint}/documentintelligence/documentModels/prebuilt-layout:analyze?api-version={ApiVersion}")
            {
                Content = new ByteArrayContent(jpeg),
            };
            request.Headers.Add("Ocp-Apim-Subscription-Key", _key);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            using var start = await Http.SendAsync(request, ct);
            if (!start.IsSuccessStatusCode)
                return new(null, null, (int)start.StatusCode switch
                {
                    429 => "Azure istek sınırı doldu (ücretsiz planda dakikada 20 / ayda 500 sayfa); biraz sonra tekrar deneyin.",
                    401 or 403 => "Azure anahtarı geçersiz (AZURE_DI_KEY).",
                    _ => $"Azure yanıt vermedi (HTTP {(int)start.StatusCode}).",
                });
            var operation = start.Headers.TryGetValues("Operation-Location", out var v) ? v.FirstOrDefault() : null;
            if (operation is null) return new(null, null, "Azure yanıtı beklenen biçimde değil.");

            // sonuç hazır olana kadar bekle. Ücretsiz planda (F0) sorgulama istekleri de dakikalık sınırdan (20) sayılır:
            // seyrek sorgulanır, sınıra takılırsa Azure'un söylediği kadar beklenir.
            var deadline = DateTime.UtcNow.AddSeconds(40);
            var wait = TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(wait, ct);
                wait = TimeSpan.FromSeconds(2.5);
                using var poll = new HttpRequestMessage(HttpMethod.Get, operation);
                poll.Headers.Add("Ocp-Apim-Subscription-Key", _key);
                using var response = await Http.SendAsync(poll, ct);
                if ((int)response.StatusCode == 429)
                {
                    wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(4);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return new(null, null, $"Azure yanıt vermedi (HTTP {(int)response.StatusCode}).");
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var status = doc.RootElement.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (status == "failed") return new(null, null, "Azure formu okuyamadı.");
                if (status != "succeeded") continue;

                var lines = Lines(doc.RootElement);
                var (code, name) = AzureFormParser.Store(lines);
                var (day, month) = AzureFormParser.Date(lines);
                var gas = AzureFormParser.Gas(Tables(doc.RootElement));
                return new(code, name, null) { Day = day, Month = month, Gas = gas };
            }
            return new(null, null, "Azure zamanında yanıt vermedi.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, null, "Azure zamanında yanıt vermedi.");
        }
        catch (HttpRequestException)
        {
            return new(null, null, "Azure'a bağlanılamadı (internet / ağ engeli).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, null, $"Azure yanıtı okunamadı: {ex.Message}");
        }
    }

    /// <summary>İlk sayfanın satırları, konumları sayfa boyutuna göre 0–1 arasına çevrilmiş olarak.</summary>
    private static List<OcrLine> Lines(JsonElement root)
    {
        var list = new List<OcrLine>();
        if (!root.TryGetProperty("analyzeResult", out var result) || !result.TryGetProperty("pages", out var pages)) return list;
        foreach (var page in pages.EnumerateArray().Take(1))
        {
            var w = page.TryGetProperty("width", out var pw) ? pw.GetDouble() : 1d;
            var h = page.TryGetProperty("height", out var ph) ? ph.GetDouble() : 1d;
            if (!page.TryGetProperty("lines", out var lines)) continue;
            foreach (var line in lines.EnumerateArray())
            {
                var text = line.GetProperty("content").GetString() ?? string.Empty;
                double x = 0, y = 0;
                if (line.TryGetProperty("polygon", out var poly) && poly.GetArrayLength() >= 8)
                {
                    var p = poly.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    x = (p[0] + p[2] + p[4] + p[6]) / 4 / w;
                    y = (p[1] + p[3] + p[5] + p[7]) / 4 / h;
                }
                list.Add(new OcrLine(text, x, y));
            }
        }
        return list;
    }

    /// <summary>Tablolar; her hücrenin güveni = hücredeki kelimelerin en düşük güveni (kelime yoksa 1).</summary>
    private static List<OcrTable> Tables(JsonElement root)
    {
        var list = new List<OcrTable>();
        if (!root.TryGetProperty("analyzeResult", out var result) || !result.TryGetProperty("tables", out var tables)) return list;
        var words = new List<(int Offset, int Length, double Confidence)>();
        if (result.TryGetProperty("pages", out var pages))
            foreach (var page in pages.EnumerateArray())
                if (page.TryGetProperty("words", out var ws))
                    foreach (var w in ws.EnumerateArray())
                        words.Add((w.GetProperty("span").GetProperty("offset").GetInt32(), w.GetProperty("span").GetProperty("length").GetInt32(),
                                   w.TryGetProperty("confidence", out var c) ? c.GetDouble() : 1d));
        foreach (var table in tables.EnumerateArray())
        {
            var cells = new List<OcrCell>();
            foreach (var cell in table.GetProperty("cells").EnumerateArray())
            {
                double conf = 1d;
                if (cell.TryGetProperty("spans", out var spans))
                    foreach (var span in spans.EnumerateArray())
                    {
                        int from = span.GetProperty("offset").GetInt32(), to = from + span.GetProperty("length").GetInt32();
                        foreach (var w in words.Where(w => w.Offset >= from && w.Offset < to)) conf = Math.Min(conf, w.Confidence);
                    }
                cells.Add(new OcrCell(cell.GetProperty("rowIndex").GetInt32(), cell.GetProperty("columnIndex").GetInt32(),
                    cell.TryGetProperty("content", out var ct) ? ct.GetString() ?? string.Empty : string.Empty, conf));
            }
            list.Add(new OcrTable(cells));
        }
        return list;
    }

    /// <summary>Açık sayfayı JPEG'e çevirir (görselde dosyanın kendisi; Azure'un kabul etmediği biçimler de JPEG olur).</summary>
    private static byte[]? PageJpeg(string path, int pageIndex)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        using var bmp = ImageExtensions.Contains(ext)
            ? SKBitmap.Decode(path)
            : PdfiumGate.Run(() => PDFtoImage.Conversion.ToImage(File.ReadAllBytes(path), page: pageIndex, options: new PDFtoImage.RenderOptions(Dpi: 150)));
        if (bmp is null) return null;
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }
}
