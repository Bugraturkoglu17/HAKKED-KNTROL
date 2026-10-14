using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using HakedisOtomasyon.Application;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace HakedisOtomasyon.Web;

/// <summary>
/// Servis formunun sağ üstündeki form numarasını Windows'un yerleşik OCR'ı ile okur (çevrimdışı, yapay zekâ yok).
///   1) Sayfa 200 DPI çizilir; sağ yarının üst %35'i kesilir (taramada sayfa kaymış olsa da numara bu bölgede kalır).
///   2) Önce yalnız KIRMIZI mürekkep bırakılır (INTİKOŞ formlarında numara kırmızı damgadır) ve okunur.
///   3) Kırmızıda bulunamazsa normal görüntü okunur (siyah-beyaz tarama; OKUR formlarında pembe basılı "005027").
/// Satırlardan numarayı <see cref="FormNumberParser"/> seçer (adres/telefon satırları elenir).
/// </summary>
public sealed class WindowsFormNumberReader : IFormNumberReader
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    private static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public Task<int> GetPageCountAsync(string absoluteFilePath) => Task.Run(() =>
    {
        try
        {
            if (!File.Exists(absoluteFilePath)) return 0;
            if (IsImage(absoluteFilePath)) return 1;
            if (!IsPdf(absoluteFilePath)) return 0;
            return PDFtoImage.Conversion.GetPageCount(File.ReadAllBytes(absoluteFilePath));
        }
        catch { return 0; }
    });

    public async Task<FormNumberReadResult> ReadAsync(string absoluteFilePath)
    {
        if (!File.Exists(absoluteFilePath))
            return new(null, 0, "Servis formu dosyası bulunamadı.");

        var pages = await GetPageCountAsync(absoluteFilePath);
        if (pages == 0) return new(null, 0, "Dosya okunamadı (yalnız PDF ve görsel dosyalar desteklenir).");
        if (pages > 1) return new(null, pages, $"Dosya {pages} sayfalı. Otomatik doldurma yalnızca tek sayfalık servis formunda çalışır.");

        var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return new(null, pages, "Bu bilgisayarda Windows metin tanıma (OCR) dili yüklü değil. "
                + "Ayarlar > Saat ve dil > Dil ve bölge > Dil ekle bölümünden İngilizce (ABD) ekleyip uygulamayı yeniden açın.");

        try
        {
            using var page = await Task.Run(() => Render(absoluteFilePath));
            if (page is null) return new(null, pages, "Sayfa görüntüye çevrilemedi.");

            var rect = new SKRectI((int)(page.Width * 0.50), 0, page.Width, (int)(page.Height * 0.35));
            using var crop = new SKBitmap(rect.Width, rect.Height);
            if (!page.ExtractSubset(crop, rect)) return new(null, pages, "Sayfa görüntüsü işlenemedi.");

            using (var red = await Task.Run(() => RedInkOnly(crop)))
            using (var redBig = red.Resize(new SKImageInfo(red.Width * 2, red.Height * 2), SKFilterQuality.High))
            {
                var hit = FormNumberParser.Pick(await RecognizeLinesAsync(engine, redBig), minDigits: 4);
                if (hit is not null) return new(hit, pages, null);
            }

            using var big = crop.Resize(new SKImageInfo(crop.Width * 3 / 2, crop.Height * 3 / 2), SKFilterQuality.High);
            // düz okumada en az 5 hane (ya da 0 ile başlayan 6–7 hane): kaşe/el yazısı 4 haneli sayılar numara sanılmasın
            var plain = FormNumberParser.Pick(await RecognizeLinesAsync(engine, big), minDigits: 5);
            return plain is not null
                ? new(plain, pages, null)
                : new(null, pages, "Form numarası okunamadı. Numarayı elle girin.");
        }
        catch (Exception ex)
        {
            return new(null, pages, $"Okuma hatası: {ex.Message}");
        }
    }

    private static SKBitmap? Render(string path)
    {
        if (IsImage(path)) return SKBitmap.Decode(path);
        var bytes = File.ReadAllBytes(path);
        return PDFtoImage.Conversion.ToImage(bytes, page: 0, options: new PDFtoImage.RenderOptions(Dpi: 200));
    }

    /// <summary>Kırmızı mürekkebi siyah, geri kalan her şeyi beyaz yapar (siyah baskılı başlık yazıları ayıklanır).</summary>
    private static SKBitmap RedInkOnly(SKBitmap src)
    {
        var pixels = src.Pixels;
        var output = new SKColor[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            bool red = c.Red > 110 && c.Red > c.Green * 1.5 && c.Red > c.Blue * 1.3;
            output[i] = red ? SKColors.Black : SKColors.White;
        }
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        dst.Pixels = output;
        return dst;
    }

    private static async Task<IReadOnlyList<string>> RecognizeLinesAsync(OcrEngine engine, SKBitmap bmp)
    {
        using var bgra = bmp.Copy(SKColorType.Bgra8888);
        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.Bytes.AsBuffer(), BitmapPixelFormat.Bgra8,
            bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(softwareBitmap);
        return result.Lines.Select(l => l.Text).ToList();
    }
}
