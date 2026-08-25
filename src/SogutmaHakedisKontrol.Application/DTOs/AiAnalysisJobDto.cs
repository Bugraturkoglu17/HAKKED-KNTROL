using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Application.DTOs;

public class AiAnalysisJobDto
{
    public int Id { get; set; }
    public int ProgressPaymentCheckId { get; set; }
    public string? ServiceFormsFileName { get; set; }
    public string? MaintenanceFormsFileName { get; set; }
    public AiJobStatus Status { get; set; }
    public string? CurrentStepDescription { get; set; }
    public int TotalServiceFormPages { get; set; }
    public int TotalMaintenancePages { get; set; }
    public int ProcessedPages { get; set; }
    public int FailedPages { get; set; }
    public int ManualReviewPages { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public int TotalPages => TotalServiceFormPages + TotalMaintenancePages;

    // ── Sonuç özet kartları (bkz. spec §12) ─────────────────────────────
    public int MatchedStoreCount { get; set; }
    public int ManualReviewDocumentCount { get; set; }

    // ── AI'nın görsel olarak sınıflandırdığı sayfa dağılımı (bkz. spec: sayfalar
    // PDF içindeki sıraya/yükleme slotuna göre değil, her sayfa bağımsız sınıflandırılır) ──
    public int SummaryPageCount { get; set; }
    public int ClassifiedServiceFormPageCount { get; set; }
    public int ClassifiedMaintenancePageCount { get; set; }
    public int UnknownPageCount { get; set; }
    public int UygunItemCount { get; set; }
    public int UygunDegilItemCount { get; set; }
    public int EksikItemCount { get; set; }
    public int FazlaItemCount { get; set; }
    public int RejectedServiceFeeCount { get; set; }
    public int ManHoursDiscrepancyCount { get; set; }

    public string StatusLabel => Status switch
    {
        AiJobStatus.Pending => "Bekliyor",
        AiJobStatus.Splitting => "PDF hazırlanıyor",
        AiJobStatus.Analyzing => "Analiz ediliyor",
        AiJobStatus.Matching => "Mağazalar eşleştiriliyor",
        AiJobStatus.Comparing => "Hakediş karşılaştırılıyor",
        AiJobStatus.Completed => "Tamamlandı",
        AiJobStatus.CompletedWithErrors => "Hatalarla tamamlandı",
        AiJobStatus.Failed => "Başarısız",
        _ => Status.ToString()
    };
}

/// <summary>Canlı ilerleme bildirimi — IProgress&lt;AiJobProgressUpdate&gt; üzerinden UI'ya akar.</summary>
public class AiJobProgressUpdate
{
    public AiJobStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? Current { get; set; }
    public int? Total { get; set; }
}

public class AiDocumentPageDto
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string? SourceFileName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;

    public string? StoreCodeRaw { get; set; }
    public string? StoreNameRaw { get; set; }
    public decimal? StoreConfidence { get; set; }
    public int? MatchedStoreId { get; set; }
    public string? MatchedStoreLabel { get; set; }
    public string StoreMatchMethod { get; set; } = string.Empty;

    public string? FormNumber { get; set; }
    public DateTime? ServiceDate { get; set; }
    public DateTime? MaintenanceDate { get; set; }
    public string? DescriptionRaw { get; set; }
    public string? WorkPerformedRaw { get; set; }

    public decimal? FormTotalHoursRaw { get; set; }
    public decimal? CalculatedManHours { get; set; }
    public decimal? PayableManHours { get; set; }
    public bool? FormTotalMatch { get; set; }
    public bool ServiceFeeRejectedDueToMaintenance { get; set; }

    public string? ErrorMessage { get; set; }
    public bool RequiresManualReview { get; set; }
    public string? ManualReviewReason { get; set; }

    public List<AiPageEmployeeDto> Employees { get; set; } = new();
    public List<AiPageMaterialDto> Materials { get; set; } = new();
}

public class AiPageEmployeeDto
{
    public int Id { get; set; }
    public string? NameRaw { get; set; }
    public string? StartTimeRaw { get; set; }
    public string? EndTimeRaw { get; set; }
    public decimal? HoursWorked { get; set; }
    public decimal? Confidence { get; set; }
}

public class AiPageMaterialDto
{
    public int Id { get; set; }
    public string RawName { get; set; } = string.Empty;
    public string? NormalizedName { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? Confidence { get; set; }
    public bool RequiresManualReview { get; set; }
    public decimal? UserCorrectedQuantity { get; set; }
    public string? UserCorrectedUnit { get; set; }
}

public class AiComparisonResultDto
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string StoreLabel { get; set; } = string.Empty;
    public DateTime? VisitDate { get; set; }
    public string? FormReference { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? FormValue { get; set; }
    public string? HakedisValue { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
}
