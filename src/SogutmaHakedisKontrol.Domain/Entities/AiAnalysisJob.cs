using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// "Yapay Zeka ile Kontrol Et" ile başlatılan tek bir analiz işlemi.
/// Bir ProgressPaymentCheck'i (hakediş kontrolünü) zenginleştirir — onun yerine geçmez.
/// </summary>
public class AiAnalysisJob
{
    public int Id { get; set; }
    public int ProgressPaymentCheckId { get; set; }

    public string? ServiceFormsFileName { get; set; }
    public string? ServiceFormsFilePath { get; set; }
    public string? MaintenanceFormsFileName { get; set; }
    public string? MaintenanceFormsFilePath { get; set; }

    public AiJobStatus Status { get; set; } = AiJobStatus.Pending;
    public string? CurrentStepDescription { get; set; }

    public int TotalServiceFormPages { get; set; }
    public int TotalMaintenancePages { get; set; }
    public int ProcessedPages { get; set; }
    public int FailedPages { get; set; }
    public int ManualReviewPages { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    public ProgressPaymentCheck ProgressPaymentCheck { get; set; } = null!;
    public ICollection<AiDocumentPage> Pages { get; set; } = new List<AiDocumentPage>();
    public ICollection<AiComparisonResult> ComparisonResults { get; set; } = new List<AiComparisonResult>();
}
