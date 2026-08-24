using HakedisOtomasyon.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace HakedisOtomasyon.Infrastructure.PdfProcessing;

public class PdfPreviewService : IPdfPreviewService
{
    private readonly ILogger<PdfPreviewService> _logger;
    private static readonly string[] PdfExtensions = [".pdf"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    public PdfPreviewService(ILogger<PdfPreviewService> logger) => _logger = logger;

    public bool IsPdf(string filePath) =>
        PdfExtensions.Contains(Path.GetExtension(filePath).ToLower());

    public bool IsImage(string filePath) =>
        ImageExtensions.Contains(Path.GetExtension(filePath).ToLower());

    public async Task<byte[]?> RenderFirstPageAsync(string filePath, int widthPx = 800)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("PDF render için dosya bulunamadı: {Path}", filePath);
            return null;
        }

        try
        {
            if (IsImage(filePath))
            {
                return await File.ReadAllBytesAsync(filePath);
            }

            if (!IsPdf(filePath))
            {
                _logger.LogWarning("Desteklenmeyen dosya türü: {Path}", filePath);
                return null;
            }

            var pdfBytes = await File.ReadAllBytesAsync(filePath);

            // PDFtoImage kütüphanesi ile ilk sayfayı render et
            var images = PDFtoImage.Conversion.ToImages(pdfBytes);
            var firstImage = images.FirstOrDefault();
            if (firstImage is null) return null;

            using var ms = new MemoryStream();
            firstImage.Encode(ms, SkiaSharp.SKEncodedImageFormat.Png, 95);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF render hatası: {Path}", filePath);
            return null;
        }
    }
}
