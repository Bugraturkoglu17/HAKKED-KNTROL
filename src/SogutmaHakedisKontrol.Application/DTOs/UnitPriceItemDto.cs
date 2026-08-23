namespace SogutmaHakedisKontrol.Application.DTOs;

public class UnitPriceItemDto
{
    public int Id { get; set; }
    public int UnitPriceListId { get; set; }
    public string? ItemCode { get; set; }
    public string? Category { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Spec { get; set; }
    public string? Unit { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? SourceFileName { get; set; }
    public int? SourceRowNumber { get; set; }
    public bool IsManuallyAdded { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Spec) ? MaterialName : $"{MaterialName} — {Spec}";
}

public class UnitPriceItemAuditLogDto
{
    public int Id { get; set; }
    public int UnitPriceItemId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
}
