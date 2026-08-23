namespace SogutmaHakedisKontrol.Application.DTOs;

public class UnitPriceListDto
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SourceFileName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int TotalItems { get; set; }
    public bool HasEurItems { get; set; }
}
