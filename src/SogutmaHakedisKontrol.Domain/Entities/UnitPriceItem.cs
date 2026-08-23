namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Onaylı birim fiyat listesindeki tek bir kalem (malzeme veya işçilik/hizmet).
/// </summary>
public class UnitPriceItem
{
    public int Id { get; set; }
    public int UnitPriceListId { get; set; }

    public string? ItemCode { get; set; }        // MALZEME KODU (firmanınkiyle örtüşmeyebilir, sadece referans)
    public string? Category { get; set; }         // ÜRÜN TİPİ (ör. "EVAPORATÖR FAN", "İŞÇİLİKLER")
    public string MaterialName { get; set; } = string.Empty; // MALZEME
    public string? Brand { get; set; }             // MARKA
    public string? Spec { get; set; }              // TİP — teknik ölçü/model, eşleştirmede kritik
    public string? Unit { get; set; }

    public decimal Price { get; set; }             // Currency cinsinden baz fiyat
    public string Currency { get; set; } = "EUR";  // "EUR" | "TRY"

    public string NormalizedName { get; set; } = string.Empty; // normalize(MaterialName + " " + Spec)

    public string? SourceFileName { get; set; }
    public int? SourceRowNumber { get; set; }

    public bool IsManuallyAdded { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    public UnitPriceList UnitPriceList { get; set; } = null!;
    public ICollection<MaterialAlias> Aliases { get; set; } = new List<MaterialAlias>();
}
