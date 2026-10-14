using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HakedisOtomasyon.Application.Interfaces;
using SkiaSharp;

namespace HakedisOtomasyon.Web;

/// <summary>Çok sayfalı PDF'in sayfa bilgisi: sayfa sayısı ve her sayfanın en/boy oranı (yükseklik / genişlik).</summary>
public sealed record PdfPagesInfo(int PageCount, IReadOnlyList<double> HeightRatios);

/// <summary>
/// Çok sayfalı servis formu PDF'lerini sayfa sayfa JPEG'e çevirir (kaydırırken hangi sayfanın görüldüğünü bilebilmek için
/// tarayıcının yerleşik PDF görüntüleyicisi yerine kullanılır; yerleşik görüntüleyici açık sayfayı bildirmez).
/// Sayfalar istendikçe (ekrana yaklaştıkça) çizilir ve %LOCALAPPDATA%\HakedisOtomasyon\PdfSayfalari altında saklanır;
/// "https://pdfpages.hakedis/" sanal adresinden gösterilir. 14 günden eski önbellek açılışta silinir.
/// </summary>
public sealed class PdfPageImageService
{
    public const string VirtualHost = "pdfpages.hakedis";
    private const int Dpi = 150;

    public static string CacheRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HakedisOtomasyon", "PdfSayfalari");

    private readonly IFileStorageService _files;
    private readonly ConcurrentDictionary<string, PdfPagesInfo> _info = new();
    private readonly ConcurrentDictionary<string, Task<bool>> _rendering = new();
    // PDFium aynı anda tek iş yapar; bellek için son açılan PDF'in baytları tutulur
    private readonly SemaphoreSlim _pdfium = new(1, 1);
    private (string Path, byte[] Bytes)? _lastPdf;

    public PdfPageImageService(IFileStorageService files)
    {
        _files = files;
        Directory.CreateDirectory(CacheRoot);
        _ = Task.Run(CleanupOldCache);
    }

    /// <summary>Sayfa sayısı ve oranları (sayfaların yerini önceden ayırmak için; kaydırma çubuğu zıplamasın).</summary>
    public async Task<PdfPagesInfo> GetInfoAsync(string storedPath)
    {
        var (abs, key) = Resolve(storedPath);
        if (_info.TryGetValue(key, out var cached)) return cached;
        await _pdfium.WaitAsync();
        try
        {
            var sizes = await Task.Run(() => PDFtoImage.Conversion.GetPageSizes(Bytes(abs)));
            var info = new PdfPagesInfo(sizes.Count, sizes.Select(s => s.Width > 0 ? s.Height / (double)s.Width : 1.414).ToList());
            _info[key] = info;
            return info;
        }
        finally { _pdfium.Release(); }
    }

    /// <summary>Sayfanın (0 tabanlı) görsel adresi; görsel yoksa önce çizilir. Çizilemezse null.</summary>
    public async Task<string?> GetPageUrlAsync(string storedPath, int pageIndex)
    {
        var (abs, key) = Resolve(storedPath);
        var name = $"p{pageIndex + 1:0000}.jpg";
        var file = Path.Combine(CacheRoot, key, name);
        var ok = File.Exists(file) || await _rendering.GetOrAdd(file, _ => RenderAsync(abs, pageIndex, file));
        _rendering.TryRemove(file, out _);
        return ok ? $"https://{VirtualHost}/{key}/{name}" : null;
    }

    private async Task<bool> RenderAsync(string abs, int pageIndex, string file)
    {
        await _pdfium.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var bmp = PDFtoImage.Conversion.ToImage(Bytes(abs), page: pageIndex, options: new PDFtoImage.RenderOptions(Dpi: Dpi));
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var tmp = file + ".tmp";
                File.WriteAllBytes(tmp, data.ToArray());
                File.Move(tmp, file, overwrite: true);
                return true;
            });
        }
        catch { return false; }
        finally { _pdfium.Release(); }
    }

    private byte[] Bytes(string abs)
    {
        if (_lastPdf is { } last && last.Path == abs) return last.Bytes;
        var bytes = File.ReadAllBytes(abs);
        _lastPdf = (abs, bytes);
        return bytes;
    }

    /// <summary>Dosyanın mutlak yolu + önbellek anahtarı (yol, boyut ve değişiklik zamanından; dosya değişirse yeniden çizilir).</summary>
    private (string Abs, string Key) Resolve(string storedPath)
    {
        var abs = _files.GetAbsolutePath(storedPath);
        var fi = new FileInfo(abs);
        if (!fi.Exists) throw new FileNotFoundException("Servis formu dosyası bulunamadı.", abs);
        var raw = $"{abs.ToUpperInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
        return (abs, key);
    }

    private static void CleanupOldCache()
    {
        try
        {
            foreach (var dir in new DirectoryInfo(CacheRoot).GetDirectories())
                if (dir.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14)) dir.Delete(recursive: true);
        }
        catch { /* önbellek temizliği kritik değil */ }
    }
}
