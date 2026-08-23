namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Birim fiyat kalemi üzerinde yapılan elle değişikliklerin denetim kaydı.
/// </summary>
public class UnitPriceItemAuditLog
{
    public int Id { get; set; }
    public int UnitPriceItemId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Note { get; set; } = string.Empty; // ör: "Bakır Boru 3/8 fiyatı 4,20 EUR → 4,50 EUR olarak değiştirildi."
    public DateTime ChangedAt { get; set; } = DateTime.Now;
}
