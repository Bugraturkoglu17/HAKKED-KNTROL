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

    // ItemType=Material satırlarında, servis formunda eşleşen AiPageMaterial'in kimliği — UI'da "Düzelt"
    // butonunun hangi malzemeyi düzelteceğini bilmesi için (bkz. AiAnalysisPipelineService.CorrectMaterialAsync).
    // Eşleşen malzeme yoksa (ör. "Eksik" durumu) null kalır — o satır için düzeltilecek bir şey yoktur.
    public int? MatchedMaterialId { get; set; }

    public AiComparisonItemType ItemType { get; set; }
    public string Description { get; set; } = string.Empty;

    public string? FormValue { get; set; }
    public string? HakedisValue { get; set; }

    public AiComparisonStatus Status { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // ── İkincil kontrol (bkz. GlycolUsageComparisonStrategy) — satırın ANA konusu bir Mağaza/Tarih
    // uyuşmazlığıysa bile (Description/Status o sorunu yansıtır), tek kalemli kategorilerde (Glikol/Gaz)
    // asıl miktar karşılaştırması AYNI SATIRDA bağımsız olarak taşınır — böylece UI hem "Tarih Uyuşmazlığı"
    // uyarısını hem de gerçek Glikol Miktarı'nı aynı anda gösterebilir. Null ise ikincil kontrol yok/gerekmiyor.
    public string? SecondaryFormValue { get; set; }
    public string? SecondaryHakedisValue { get; set; }
    public AiComparisonStatus? SecondaryStatus { get; set; }

    // ── Kullanıcı manuel onayı (bkz. AiComparisonOverride) — recompute'ta bu satır silinip yeniden
    // üretildiğinde AiAnalysisPipelineService.ApplyOverridesAsync tarafından yeniden uygulanır. ──
    public bool UserOverridden { get; set; }
    public AiComparisonStatus? OriginalStatus { get; set; }
    public string? OverrideNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public AiAnalysisJob Job { get; set; } = null!;
}
