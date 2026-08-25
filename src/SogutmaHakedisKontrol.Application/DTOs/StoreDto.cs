namespace SogutmaHakedisKontrol.Application.DTOs;

public class StoreDto
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? StoreRegion { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Mağaza listesi Excel'i içe aktarma önizlemesi.</summary>
public class StoreImportPreviewDto
{
    public int TotalStores { get; set; }
    public int DuplicateCodeCount { get; set; }
    public List<string> DuplicateCodes { get; set; } = new();
    public int ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<StoreDto> Items { get; set; } = new();
}
