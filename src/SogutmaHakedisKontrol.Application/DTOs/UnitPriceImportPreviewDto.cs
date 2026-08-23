namespace SogutmaHakedisKontrol.Application.DTOs;

/// <summary>Birim fiyat Excel'i içe aktarma öncesi önizleme sonucu.</summary>
public class UnitPriceImportPreviewDto
{
    public int TotalItems { get; set; }
    public int EurItemCount { get; set; }
    public int TryItemCount { get; set; }
    public int MissingPriceCount { get; set; }
    public int DuplicateNameCount { get; set; }
    public List<string> DuplicateNames { get; set; } = new();
    public int SheetCount { get; set; }
    public int TotalRowsRead { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> DebugMessages { get; set; } = new();
    public List<UnitPriceItemDto> Items { get; set; } = new();
}
