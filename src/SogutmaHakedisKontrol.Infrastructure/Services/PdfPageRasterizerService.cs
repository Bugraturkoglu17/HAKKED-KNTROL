using PDFtoImage;
using SkiaSharp;
using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// PDF sayfalarını yüksek çözünürlüklü PNG'ye dönüştürür. El yazılı formlar için düşük DPI OCR
/// yaklaşımı kullanılmaz — varsayılan 220 DPI, gerektiğinde artırılabilir.
/// </summary>
public class PdfPageRasterizerService : IPdfPageRasterizer
{
    public List<byte[]> RasterizeToPngPages(byte[] pdfBytes, int dpi = 220)
    {
        var result = new List<byte[]>();
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: true);

        foreach (var bitmap in Conversion.ToImages(pdfBytes, password: null, options: options))
        {
            using (bitmap)
            {
                using var ms = new MemoryStream();
                bitmap.Encode(ms, SKEncodedImageFormat.Png, 100);
                result.Add(ms.ToArray());
            }
        }
        return result;
    }

    public int GetPageCount(byte[] pdfBytes) => Conversion.GetPageCount(pdfBytes);

    public List<byte[]> RasterizeDocumentToPngPages(byte[] fileBytes, string fileName, int dpi = 220)
    {
        if (IsImageFile(fileName, fileBytes))
            return new List<byte[]> { ConvertToPng(fileBytes) };

        return RasterizeToPngPages(fileBytes, dpi);
    }

    private static bool IsImageFile(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png") return true;
        if (ext == ".pdf") return false;

        // Uzantı belirsiz/eksikse magic byte ile karar ver.
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
            return false; // "%PDF"
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return true; // PNG
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true; // JPEG
        return false;
    }

    private static byte[] ConvertToPng(byte[] imageBytes)
    {
        using var bitmap = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("Görsel dosyası okunamadı veya bozuk.");
        using var ms = new MemoryStream();
        bitmap.Encode(ms, SKEncodedImageFormat.Png, 100);
        return ms.ToArray();
    }
}
