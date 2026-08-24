using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Domain.Entities;

public class PriceItem
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
    public PriceType PriceType { get; set; } = PriceType.FixedPrice;
    public string? SearchText { get; set; }
    public bool IsSelectable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsManuallyAdded { get; set; } = false;
    public bool HasMissingUnit { get; set; } = false;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;

    /// <summary>İçeri aktarma notu — örn. "TAHMİNİ FİYAT - kesin onay gerekir."</summary>
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    // ── Döviz bazlı ürünler (BDTX / BDKF) ───────────────────────────────────
    public bool IsCurrencyBased { get; set; }
    public string? CurrencyCode { get; set; }        // "USD"
    public decimal? ListPriceUsd { get; set; }       // BVN Liste fiyatı (USD)
    public decimal? DiscountRate { get; set; }       // Yüzde olarak, ör. 20
    public decimal? DiscountedUsdPrice { get; set; } // ListPriceUsd × (1 - DiscountRate/100)
    public bool ExchangeRateRequired { get; set; }   // TL kur hesabı gerektiriyor

    public decimal UnitPrice => MaterialPrice + LaborPrice;
}
