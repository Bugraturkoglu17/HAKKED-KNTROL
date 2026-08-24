using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Domain.Entities;

public class ServiceForm
{
    public int Id { get; set; }
    public int ProgressClaimId { get; set; }
    public int? StoreId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.Now;
    public ServiceFormStatus Status { get; set; } = ServiceFormStatus.Bekliyor;
    public string? Notes { get; set; }

    // ── Arşiv / Hash alanı (yeni) ──
    public string? FileHash { get; set; }          // SHA256 hex
    public long FileSize { get; set; }              // bayt
    public bool IsArchived { get; set; } = false;
    public DateTime? ArchivedAt { get; set; }

    public ProgressClaim ProgressClaim { get; set; } = null!;
    public Store? Store { get; set; }
    public ICollection<ServiceItem> ServiceItems { get; set; } = new List<ServiceItem>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}
