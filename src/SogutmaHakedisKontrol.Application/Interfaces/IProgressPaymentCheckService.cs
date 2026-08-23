using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IProgressPaymentCheckService
{
    Task<List<ProgressPaymentCheckDto>> GetHistoryAsync();
    Task<ProgressPaymentCheckDto?> GetByIdAsync(int id);
    Task<List<ProgressPaymentCheckItemDto>> GetItemsAsync(int checkId);

    Task<ProgressPaymentImportPreviewDto> ParseExcelAsync(Stream stream, string fileName, int unitPriceListId);

    /// <summary>Excel'i içe aktarır, otomatik eşleştirmeyi (alias + exact + fuzzy skorlama) çalıştırır ve kaydeder.
    /// Orijinal dosya baytları değiştirilmeden ayrı bir konuma kopyalanır (asla üzerine yazılmaz).</summary>
    Task<ProgressPaymentCheckDto> CreateCheckAsync(
        int unitPriceListId, string companyName, string region, string claimTypeName,
        int year, int month, string periodLabel,
        string originalFileName, byte[] originalFileBytes,
        decimal? exchangeRateEur,
        ProgressPaymentImportPreviewDto parsed);

    /// <summary>Turuncu kuyruktaki bekleyen (henüz karar verilmemiş) grupları döner.</summary>
    Task<List<MaterialMatchQueueEntryDto>> GetPendingMatchQueueAsync(int checkId);

    /// <summary>Kullanıcının "Evet/Manuel Seç" kararını, aynı orijinal ada sahip tüm satırlara uygular ve alias kaydeder.</summary>
    Task ResolveMatchAsync(int checkId, List<int> checkItemIds, int unitPriceItemId, bool saveAsAlias, string? companyName);

    /// <summary>Kullanıcının "Hayır / eşleşme yok" kararı — satırları eşleşmemiş bırakır.</summary>
    Task RejectMatchAsync(int checkId, List<int> checkItemIds);

    Task ExcludeItemAsync(int checkItemId, bool excluded);
    Task CorrectQuantityAsync(int checkItemId, decimal newQuantity);

    /// <summary>Tüm satırların fiyat/tutar hesaplarını (kur dahil) yeniden çalıştırır ve check toplamlarını günceller.</summary>
    Task RecalculateAsync(int checkId);

    Task<ProgressPaymentCheckDto> FinalizeAsync(int checkId);

    /// <summary>Kontrol edilmiş Excel kopyasını üretir (orijinal dosya bozulmadan, ek kolonlarla) ve yolunu döner.</summary>
    Task<string> ExportControlledExcelAsync(int checkId);
}
