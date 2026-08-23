namespace SogutmaHakedisKontrol.Application.DTOs;

/// <summary>Firma hakediş Excel'inin okunması sonucu — henüz kontrol/eşleştirme yapılmadan.</summary>
public class ProgressPaymentImportPreviewDto
{
    public string DetectedClaimTypeName { get; set; } = string.Empty;
    public int? DetectedYear { get; set; }
    public int? DetectedMonth { get; set; }
    public string? DetectedPeriodLabel { get; set; }
    public string? DetectedCompanyName { get; set; }
    public decimal? DetectedCompanyGrandTotal { get; set; } // GENEL ICMAL toplamı

    public int TotalRowsRead { get; set; }
    public int MaterialLineCount { get; set; }
    public int ServiceLineCount { get; set; }
    public int StoreCount { get; set; }
    public int MissingQuantityCount { get; set; }

    public string? SuggestedEurRate { get; set; }
    public string? SuggestedEurRateSource { get; set; }

    public List<string> Errors { get; set; } = new();
    public List<string> DebugMessages { get; set; } = new();

    public List<ProgressPaymentCheckItemDto> Items { get; set; } = new();
}
