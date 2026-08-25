using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Hakediş dosyasındaki tek bir satırın kontrol sonucu.
/// Orijinal (firma) bilgisi hiçbir zaman kaybolmaz; eşleştirme/hesap ayrı alanlarda tutulur.
/// </summary>
public class ProgressPaymentCheckItem
{
    public int Id { get; set; }
    public int ProgressPaymentCheckId { get; set; }

    public string? SheetName { get; set; }
    public int? SourceRowNumber { get; set; }

    // ── Orijinal Excel hücre adresleri (Düzelt için gerçek hücreyi bulmakta kullanılır) ──
    public string? MaterialCellRef { get; set; }
    public string? QuantityCellRef { get; set; }
    public string? UnitPriceCellRef { get; set; }
    public string? LineTotalCellRef { get; set; }

    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public string? StoreFormat { get; set; }
    public int? MatchedStoreId { get; set; }   // Mağaza ana listesiyle eşleşme (AI karşılaştırması için)
    public DateTime? VisitDate { get; set; }
    public string? MaintenanceFormNo { get; set; }

    // ── Firmanın orijinal verisi (asla değiştirilmez) ──────────────────
    public string? OriginalItemCode { get; set; }
    public string OriginalMaterialName { get; set; } = string.Empty;
    public string? OriginalMaterialSpec { get; set; }
    public bool IsServiceItem { get; set; }

    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal CompanyUnitPrice { get; set; }
    public decimal CompanyLineTotal { get; set; }

    // ── Eşleştirme ──────────────────────────────────────────────────────
    public int? MatchedUnitPriceItemId { get; set; }
    public string? MatchedMaterialName { get; set; } // snapshot
    public decimal? MatchConfidence { get; set; }     // 0-1
    public MaterialMatchStatus MatchStatus { get; set; } = MaterialMatchStatus.Unmatched;

    // ── Hesaplama ───────────────────────────────────────────────────────
    public decimal? ApprovedUnitPrice { get; set; }     // liste para biriminde (EUR/TRY)
    public string? ApprovedCurrency { get; set; }
    public decimal? ApprovedUnitPriceTry { get; set; }   // kur uygulanmış TL karşılığı
    public decimal? CalculatedLineTotal { get; set; }
    public decimal? Difference { get; set; }              // CompanyLineTotal - CalculatedLineTotal
    public decimal? DifferencePercent { get; set; }

    public bool UnitMismatch { get; set; }

    public CheckItemControlStatus ControlStatus { get; set; } = CheckItemControlStatus.KontrolGerekli;
    public string? ControlNote { get; set; }

    public bool IsExcluded { get; set; }
    public bool QuantityManuallyCorrected { get; set; }

    /// <summary>Kullanıcı "Düzelt" dedi — export'ta UnitPriceCellRef'teki hücre onaylı fiyatla değiştirilip kırmızı işaretlenir.</summary>
    public bool PriceCorrectionApplied { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ProgressPaymentCheck ProgressPaymentCheck { get; set; } = null!;
}
