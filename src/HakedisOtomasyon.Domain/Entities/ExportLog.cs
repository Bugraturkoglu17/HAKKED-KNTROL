using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Domain.Entities;

public class ExportLog
{
    public int Id { get; set; }
    public int ProgressClaimId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public ExportType ExportType { get; set; }
    public string? Notes { get; set; }

    // ── Arşiv alanı (yeni) ──
    public bool IsActive { get; set; } = true;
    public bool IsArchived { get; set; } = false;
    public DateTime? ArchivedAt { get; set; }

    public ProgressClaim ProgressClaim { get; set; } = null!;
}
