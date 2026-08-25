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
}
