namespace HakedisOtomasyon.Application.DTOs;

public class DashboardSummaryDto
{
    public int CurrentYear { get; set; }
    public int CurrentMonth { get; set; }
    public int TotalForms { get; set; }
    public int ProcessedForms { get; set; }
    public int IncompleteForms { get; set; }
    public decimal TotalClaimAmount { get; set; }
    public int InvoiceItemCount { get; set; }
    public List<RecentExportDto> RecentExports { get; set; } = new();
    public int ActiveClaimsCount { get; set; }
}

public class RecentExportDto
{
    public int Id { get; set; }
    public string ClaimName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; }
    public string ExportTypeLabel { get; set; } = string.Empty;
}

public class ClaimSummaryDto
{
    public int ClaimId { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public decimal ServiceFeeTotal { get; set; }
    public decimal PriceListTotal { get; set; }
    public decimal InvoiceTotal { get; set; }
    public decimal GrandTotal => ServiceFeeTotal + PriceListTotal + InvoiceTotal;
    public int FormCount { get; set; }
    public int InvoiceCount { get; set; }
}
