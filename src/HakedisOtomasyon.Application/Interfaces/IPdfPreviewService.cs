namespace HakedisOtomasyon.Application.Interfaces;

public interface IPdfPreviewService
{
    /// <summary>PDF dosyasının ilk sayfasını PNG görüntüsüne çevirir. Byte dizisi döner.</summary>
    Task<byte[]?> RenderFirstPageAsync(string filePath, int widthPx = 800);

    bool IsPdf(string filePath);
    bool IsImage(string filePath);
}
