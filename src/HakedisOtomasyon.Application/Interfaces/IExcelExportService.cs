namespace HakedisOtomasyon.Application.Interfaces;

public interface IExcelExportService
{
    /// <summary>Belirtilen hakediş için Excel dosyası oluşturur ve dosya yolunu döner.</summary>
    Task<string> ExportClaimAsync(int claimId, IProgress<string>? progress = null);
}
