using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IFileCleanupService
{
    /// <summary>
    /// Belirtilen dosyayı arşiv klasörüne taşır; yeni göreceli yolu döner.
    /// Dosya başka bir program tarafından kullanılıyorsa IOException fırlatır.
    /// </summary>
    Task<string> ArchiveFileAsync(string relativeSourcePath, ArchiveCategory category, int year, int month);

    /// <summary>
    /// Tüm arşiv klasörlerinde belirtilen günden eski dosyaları siler.
    /// Silinen dosya sayısını döner.
    /// </summary>
    Task<int> CleanupArchiveOlderThanAsync(int days);

    /// <summary>
    /// Belirtilen hakediş için eski (aktif olmayan) Excel exportlarını arşive taşır.
    /// Yeni export kaydedilmeden hemen önce çağrılır.
    /// </summary>
    Task MoveOldExportsToArchiveAsync(int progressClaimId, int year, int month);

    /// <summary>
    /// Servis formunu DB'den arşivler; fiziksel dosyasını arşiv klasörüne taşır.
    /// </summary>
    Task ArchiveRemovedServiceFormAsync(int serviceFormId);

    /// <summary>
    /// Faturayı DB'den arşivler; fiziksel dosyasını arşiv klasörüne taşır.
    /// </summary>
    Task ArchiveRemovedInvoiceAsync(int invoiceId);

    /// <summary>
    /// Uygulama açılışında sessizce arşiv temizliği yapar.
    /// Hata olursa loglar, kullanıcıya göstermez.
    /// </summary>
    Task RunStartupCleanupAsync();
}
