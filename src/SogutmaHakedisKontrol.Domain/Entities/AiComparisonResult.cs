using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// AI'dan çıkarılan servis formu verisiyle hakediş Excel satırının karşılaştırma sonucu — sonuç tablosunun tek satırı.
/// </summary>
public class AiComparisonResult
{
    public int Id { get; set; }
    public int JobId { get; set; }

    public int? StoreId { get; set; }
    public string StoreLabel { get; set; } = string.Empty;
    public DateTime? VisitDate { get; set; }

    public int? SourcePageId { get; set; }
    public int? ProgressPaymentCheckItemId { get; set; }

    public AiComparisonItemType ItemType { get; set; }
    public string Description { get; set; } = string.Empty;

    public string? FormValue { get; set; }
    public string? HakedisValue { get; set; }

    public AiComparisonStatus Status { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // ── Kullanıcı manuel onayı (bkz. AiComparisonOverride) — recompute'ta bu satır silinip yeniden
    // üretildiğinde AiAnalysisPipelineService.ApplyOverridesAsync tarafından yeniden uygulanır. ──
    public bool UserOverridden { get; set; }
    public AiComparisonStatus? OriginalStatus { get; set; }
    public string? OverrideNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public AiAnalysisJob Job { get; set; } = null!;
}
