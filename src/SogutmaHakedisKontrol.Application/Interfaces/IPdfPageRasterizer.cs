namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>PDF sayfalarını yüksek çözünürlüklü PNG görsellerine dönüştürür (el yazısı okunabilirliği için).</summary>
public interface IPdfPageRasterizer
{
    /// <summary>Her sayfa için PNG bayt dizisi döner, sayfa sırasına göre.</summary>
    List<byte[]> RasterizeToPngPages(byte[] pdfBytes, int dpi = 220);
    int GetPageCount(byte[] pdfBytes);

    /// <summary>
    /// Dosya PDF ise sayfalara ayırıp PNG'ye çevirir; dosya doğrudan bir görsel (JPG/PNG) ise
    /// tek elemanlı bir liste olarak (PNG'ye normalize edilmiş) döner. Yükleme ekranı PDF/JPG/PNG
    /// kabul ettiği için asıl işlem noktası bu metottur — RasterizeToPngPages yalnızca PDF varsayar.
    /// </summary>
    List<byte[]> RasterizeDocumentToPngPages(byte[] fileBytes, string fileName, int dpi = 220);
}
