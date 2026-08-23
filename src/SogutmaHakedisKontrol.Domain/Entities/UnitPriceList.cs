namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Soğutma bakım hakediş kontrolü için onaylı birim fiyat listesi.
/// Firma → Bölge bazında versiyonlanabilir (ör. İNTİKOŞ / İÇ ANADOLU / 2026).
/// </summary>
public class UnitPriceList
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SourceFileName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<UnitPriceItem> Items { get; set; } = new List<UnitPriceItem>();
}
