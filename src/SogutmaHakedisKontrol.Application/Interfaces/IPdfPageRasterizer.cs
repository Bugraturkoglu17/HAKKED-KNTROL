namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>PDF sayfalarını yüksek çözünürlüklü PNG görsellerine dönüştürür (el yazısı okunabilirliği için).</summary>
public interface IPdfPageRasterizer
{
    /// <summary>Her sayfa için PNG bayt dizisi döner, sayfa sırasına göre.</summary>
    List<byte[]> RasterizeToPngPages(byte[] pdfBytes, int dpi = 220);
    int GetPageCount(byte[] pdfBytes);
}
