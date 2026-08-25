using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Application.DTOs;

public class ProgressPaymentCheckDto
{
    public int Id { get; set; }
    public int UnitPriceListId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ClaimTypeName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string OriginalFilePath { get; set; } = string.Empty;
    public decimal? ExchangeRateEur { get; set; }
    public decimal CompanyTotal { get; set; }
    public decimal CalculatedTotal { get; set; }
    public decimal Difference { get; set; }
    public ProgressPaymentCheckStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ControlledFilePath { get; set; }

    public int TotalItemCount { get; set; }
    public int UygunCount { get; set; }
    public int OnayBekliyorCount { get; set; }
    public int HataliCount { get; set; }
    public int EslesmeyenCount { get; set; }

    public string StatusLabel => Status switch
    {
        ProgressPaymentCheckStatus.Taslak => "Taslak",
        ProgressPaymentCheckStatus.EslesmeBekliyor => "Eşleşme Bekliyor",
        ProgressPaymentCheckStatus.Tamamlandi => "Tamamlandı",
        _ => Status.ToString()
    };
}

public class ProgressPaymentCheckItemDto
{
    public int Id { get; set; }
    public int ProgressPaymentCheckId { get; set; }
    public string? SheetName { get; set; }
    public int? SourceRowNumber { get; set; }
    public string? MaterialCellRef { get; set; }
    public string? QuantityCellRef { get; set; }
    public string? UnitPriceCellRef { get; set; }
    public string? LineTotalCellRef { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public string? StoreFormat { get; set; }
    public DateTime? VisitDate { get; set; }
    public string? MaintenanceFormNo { get; set; }

    public string? OriginalItemCode { get; set; }
    public string OriginalMaterialName { get; set; } = string.Empty;
    public string? OriginalMaterialSpec { get; set; }
    public bool IsServiceItem { get; set; }

    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal CompanyUnitPrice { get; set; }
    public decimal CompanyLineTotal { get; set; }

    public int? MatchedUnitPriceItemId { get; set; }
    public string? MatchedMaterialName { get; set; }
    public decimal? MatchConfidence { get; set; }
    public MaterialMatchStatus MatchStatus { get; set; }

    public decimal? ApprovedUnitPrice { get; set; }
    public string? ApprovedCurrency { get; set; }
    public decimal? ApprovedUnitPriceTry { get; set; }
    public decimal? CalculatedLineTotal { get; set; }
    public decimal? Difference { get; set; }
    public decimal? DifferencePercent { get; set; }
    public bool UnitMismatch { get; set; }

    public CheckItemControlStatus ControlStatus { get; set; }
    public string? ControlNote { get; set; }
    public bool IsExcluded { get; set; }
    public bool QuantityManuallyCorrected { get; set; }
    public bool PriceCorrectionApplied { get; set; }

    public string ControlStatusLabel => ControlStatus switch
    {
        CheckItemControlStatus.Uygun => "Uygun",
        CheckItemControlStatus.OnayBekliyor => "Onay Bekliyor",
        CheckItemControlStatus.FiyatHatasi => "Fiyat Hatası",
        CheckItemControlStatus.BirimFiyatBulunamadi => "Birim Fiyat Bulunamadı",
        CheckItemControlStatus.BirimUyusmazligi => "Birim Uyuşmazlığı",
        CheckItemControlStatus.KontrolDisi => "Kontrol Dışı",
        _ => "Kontrol Gerekli"
    };
}

/// <summary>Kullanıcı onayı bekleyen tahmini eşleşme kuyruğu için tek bir aday.</summary>
public class MaterialMatchCandidateDto
{
    public int UnitPriceItemId { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public string? Spec { get; set; }
    public string? Unit { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal Confidence { get; set; }
    public bool SpecMismatchWarning { get; set; }
}

public class MaterialMatchQueueEntryDto
{
    public List<int> CheckItemIds { get; set; } = new(); // aynı orijinal ad ile eşleşen tüm satırlar (tek soru, hepsine uygula)
    public string OriginalMaterialName { get; set; } = string.Empty;
    public string? OriginalMaterialSpec { get; set; } = string.Empty;
    public int OccurrenceCount { get; set; }
    public List<MaterialMatchCandidateDto> Candidates { get; set; } = new();
}

/// <summary>Bir hakediş kalemi üzerinde alınan kararın kalıcı kaydı (Düzelt/Geri Al/Yeni Kalem Ekle/...).</summary>
public class CheckItemActionLogDto
{
    public int Id { get; set; }
    public int ProgressPaymentCheckItemId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
