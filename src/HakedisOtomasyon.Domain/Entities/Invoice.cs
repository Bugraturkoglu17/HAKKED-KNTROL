namespace HakedisOtomasyon.Domain.Entities;

public class Invoice
{
    public int Id { get; set; }
    public int ServiceFormId { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal InvoiceAmount { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal MarkupRate { get; set; } = 0.10m;
    public decimal CalculatedTotal { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;

    // ── Arşiv / Hash alanı (yeni) ──
    public string? FileHash { get; set; }          // SHA256 hex
    public long FileSize { get; set; }              // bayt
    public bool IsArchived { get; set; } = false;
    public DateTime? ArchivedAt { get; set; }

    public ServiceForm ServiceForm { get; set; } = null!;
    public ICollection<ServiceItem> ServiceItems { get; set; } = new List<ServiceItem>();
}
