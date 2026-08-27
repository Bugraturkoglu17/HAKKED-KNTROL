using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Kullanıcının bir AiComparisonResult üzerinde verdiği kalıcı manuel karar (ör. "Uygun olarak
/// onayla"). AiComparisonResult satırları her karşılaştırma yeniden hesaplandığında (AI yeniden
/// çalıştırıldığında, malzeme düzeltildiğinde) tamamen silinip yeniden üretildiği için onay doğrudan
/// o satıra yazılamaz — bunun yerine burada, sonucu üreten kaynağa (sayfa/hakediş kalemi/kalem türü/
/// açıklama) bağlı bir MatchKey ile kalıcı olarak saklanır ve her recompute sonrası
/// AiAnalysisPipelineService.ApplyOverridesAsync tarafından yeniden uygulanır.
/// </summary>
public class AiComparisonOverride
{
    public int Id { get; set; }
    public int JobId { get; set; }

    public string MatchKey { get; set; } = string.Empty;
    public AiComparisonStatus OverrideStatus { get; set; }
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public AiAnalysisJob Job { get; set; } = null!;
}
