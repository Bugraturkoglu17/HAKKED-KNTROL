namespace HakedisOtomasyon.Application.DTOs;

public class InvoiceDto
{
    public int Id { get; set; }
    public int ServiceFormId { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public string? FileUrl { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal InvoiceAmount { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal MarkupRate { get; set; } = 0.10m;
    public decimal CalculatedTotal { get; set; }
    public DateTime UploadedAt { get; set; }

    public void Recalculate()
    {
        CalculatedTotal = InvoiceAmount * (1 + MarkupRate) * Quantity;
    }

    public string MarkupRateDisplay => $"%{MarkupRate * 100:0}";
}
