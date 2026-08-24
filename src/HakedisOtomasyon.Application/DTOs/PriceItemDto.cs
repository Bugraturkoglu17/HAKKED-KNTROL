using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.DTOs;

public class PriceItemDto
{
    public int Id { get; set; }
    public string? SourceSheetName { get; set; }
    public int? SourceRowNumber { get; set; }
    public string? PozNo { get; set; }
    public string? MainCategory { get; set; }
    public string? SubCategory { get; set; }
    public string? SubCategory2 { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? InvoiceDescription { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal MaterialPrice { get; set; }
    public decimal LaborPrice { get; set; }
    public decimal UnitPrice => MaterialPrice + LaborPrice;
    public PriceType PriceType { get; set; } = PriceType.FixedPrice;
    public bool IsSelectable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsManuallyAdded { get; set; } = false;
    public bool HasMissingUnit { get; set; } = false;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;

    /// <summary>İçeri aktarma notu — örn. "TAHMİNİ FİYAT - kesin onay gerekir."</summary>
    public string? Notes { get; set; }

    // ── Döviz bazlı ürünler (BDTX / BDKF) ───────────────────────────────────
    public bool IsCurrencyBased { get; set; }
    public string? CurrencyCode { get; set; }
    public decimal? ListPriceUsd { get; set; }       // BVN Liste fiyatı (USD)
    public decimal? DiscountRate { get; set; }       // Yüzde olarak, ör. 20
    public decimal? DiscountedUsdPrice { get; set; }
    public bool ExchangeRateRequired { get; set; }

    public string PriceTypeLabel => PriceType switch
    {
        PriceType.LaborOnly => "Sadece İşçilik",
        PriceType.MaterialOnly => "Sadece Malzeme",
        PriceType.VariablePrice => "Değişken",
        PriceType.PercentageBased => "Yüzdesel",
        _ => "Sabit"
    };

    public string GetDisplayName()
    {
        if (!string.IsNullOrEmpty(SubCategory2)) return $"{SubCategory2} > {Description}";
        if (!string.IsNullOrEmpty(SubCategory)) return $"{SubCategory} > {Description}";
        if (!string.IsNullOrEmpty(MainCategory)) return $"{MainCategory} > {Description}";
        return Description;
    }

    public string GetInvoiceDescription()
    {
        // Use InvoiceDescription only when it adds info beyond the bare Description
        if (!string.IsNullOrEmpty(InvoiceDescription) && InvoiceDescription != Description)
            return InvoiceDescription;
        if (!string.IsNullOrEmpty(SubCategory2)) return $"{SubCategory2} - {Description}";
        if (!string.IsNullOrEmpty(SubCategory)) return $"{SubCategory} - {Description}";
        if (!string.IsNullOrEmpty(MainCategory)) return $"{MainCategory} - {Description}";
        return Description;
    }
}
