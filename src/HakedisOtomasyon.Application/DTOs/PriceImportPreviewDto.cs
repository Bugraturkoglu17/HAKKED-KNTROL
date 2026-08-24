namespace HakedisOtomasyon.Application.DTOs;

public class PriceImportPreviewDto
{
    public int TotalItems { get; set; }
    public int MainCategoryCount { get; set; }
    public int SubCategoryCount { get; set; }
    public int FixedPriceCount { get; set; }
    public int LaborOnlyCount { get; set; }
    public int MaterialOnlyCount { get; set; }
    public int VariablePriceCount { get; set; }
    public int PercentageBasedCount { get; set; }
    public int MissingUnitCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<PriceItemDto> Items { get; set; } = new();

    // Debug bilgileri — 0 kalem geldiğinde kullanıcıya gösterilir
    public int SheetCount { get; set; }
    public int TotalRowsRead { get; set; }
    public int HeaderRowNumber { get; set; }
    public List<string> MatchedColumns { get; set; } = new();
    public List<string> DebugMessages { get; set; } = new();
}
