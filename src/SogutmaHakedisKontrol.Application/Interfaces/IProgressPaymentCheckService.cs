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

    /// <summary>Bir hakediş kalemine benzer, sistemde zaten kayıtlı olabilecek onaylı kalemleri döner
    /// (yeni kalem eklemeden önce mükerrer kayıt uyarısı için kullanılır).</summary>
    Task<List<MaterialMatchCandidateDto>> FindSimilarCandidatesAsync(int checkItemId);

    /// <summary>Yeni bir onaylı birim fiyat kalemi olarak gerçek DB kaydı oluşturur, verilen hakediş
    /// satırlarını bu yeni kalemle eşleştirir, alias olarak öğrenir ve kontrolü yeniden hesaplar.
    /// actionLabel denetim izinde kullanılır (ör. "YeniKalemEkle" veya "BuFiyatDogru").</summary>
    Task<UnitPriceItemDto> CreateAndMatchNewItemAsync(int checkId, List<int> checkItemIds, UnitPriceItemDto newItem, string? companyName, string actionLabel);

    /// <summary>Yanlış birim fiyatı "Düzelt" (apply=true) veya "Geri Al" (apply=false) — export sırasında
    /// orijinal Excel'in gerçek birim fiyat hücresine yazılıp yazılmayacağını belirler.</summary>
    Task SetPriceCorrectionAsync(int checkItemId, bool apply);

    /// <summary>Toplu düzeltmenin etkileyeceği kesin (belirsiz olmayan) fiyat hatası satır sayısı — onay öncesi gösterim için.</summary>
    Task<int> GetBulkPriceCorrectionPreviewCountAsync(int checkId);

    /// <summary>Kesin eşleşen ve kesin fiyat hatası olan tüm satırlara toplu "Düzelt" uygular. Belirsiz eşleşmeler kapsam dışıdır.</summary>
    Task<int> ApplyBulkPriceCorrectionAsync(int checkId);

    /// <summary>Bir kontrole ait tüm kullanıcı kararlarının (Düzelt/Geri Al/Yeni Kalem/...) denetim izi.</summary>
    Task<List<CheckItemActionLogDto>> GetActionLogAsync(int checkId);

    /// <summary>Tüm satırların fiyat/tutar hesaplarını (kur dahil) yeniden çalıştırır ve check toplamlarını günceller.</summary>
    Task RecalculateAsync(int checkId);

    Task<ProgressPaymentCheckDto> FinalizeAsync(int checkId);

    /// <summary>Kontrol edilmiş Excel kopyasını üretir (orijinal dosya bozulmadan, ek kolonlarla) ve yolunu döner.</summary>
    Task<string> ExportControlledExcelAsync(int checkId);
}
