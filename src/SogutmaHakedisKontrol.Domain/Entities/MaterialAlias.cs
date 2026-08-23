namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Firmanın hakedişte kullandığı malzeme/hizmet adının, bizim onaylı kataloğumuzdaki
/// hangi kaleme karşılık geldiğine dair öğrenilmiş/onaylanmış eşleştirme.
/// Kullanıcı onayı olmadan otomatik oluşmaz (bkz. MaterialMatchingService).
/// </summary>
public class MaterialAlias
{
    public int Id { get; set; }
    public string? CompanyName { get; set; }       // null = tüm firmalar için geçerli
    public string AliasText { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public int UnitPriceItemId { get; set; }
    public bool ApprovedByUser { get; set; } = true;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public UnitPriceItem UnitPriceItem { get; set; } = null!;
}
