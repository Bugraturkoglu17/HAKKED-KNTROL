using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IAiAnalysisPipelineService
{
    /// <summary>
    /// Servis formu (birden fazla PDF olabilir — bkz. spec) ve/veya periyodik bakım PDF'ini analiz eder,
    /// mağazaları eşleştirir, deterministic iş kurallarını (adam-saat, periyodik bakım çakışması) uygular
    /// ve mevcut hakediş kontrolüyle (ProgressPaymentCheck) karşılaştırır.
    /// İlerleme <paramref name="progress"/> üzerinden canlı raporlanır.
    /// </summary>
    Task<AiAnalysisJobDto> RunAsync(
        int progressPaymentCheckId,
        IReadOnlyList<(byte[] Bytes, string FileName)> serviceForms,
        byte[]? maintenanceFormsPdf, string? maintenanceFormsFileName,
        IProgress<AiJobProgressUpdate>? progress,
        CancellationToken cancellationToken = default);

    Task<AiAnalysisJobDto?> GetJobAsync(int jobId);
    Task<AiAnalysisJobDto?> GetLatestJobForCheckAsync(int progressPaymentCheckId);
    Task<List<AiDocumentPageDto>> GetPagesAsync(int jobId);
    Task<List<AiComparisonResultDto>> GetComparisonResultsAsync(int jobId);

    /// <summary>Kategoriden bağımsız mağaza/form eşleşme özetinin detay satırları (eksik mağaza,
    /// fazla/yabancı mağaza, eşleşmeyen form) — sayısal özet <see cref="AiAnalysisJobDto"/> üzerinde.</summary>
    Task<List<StoreReconciliationIssueDto>> GetStoreReconciliationIssuesAsync(int jobId);

    /// <summary>Kullanıcının manuel düzelttiği malzeme miktarını kaydeder (AI'nın orijinal değeri korunur).</summary>
    Task CorrectMaterialAsync(int materialId, decimal? correctedQuantity, string? correctedUnit, string? note);

    /// <summary>Belirsiz mağaza eşleşmesini kullanıcı elle onaylar/düzeltir.</summary>
    Task CorrectPageStoreAsync(int pageId, int storeId);

    /// <summary>Kullanıcı, AI'nin "Uygun" dışı verdiği bir sonucu manuel inceleme sonrası "Uygun" olarak
    /// onaylar — kalıcıdır (AiComparisonOverride), sonuç recompute ile silinip yeniden üretilse bile
    /// tekrar uygulanır. Export'ta bu satır artık "Uygun" sayıldığı için kontrol notu almaz.</summary>
    Task OverrideResultStatusAsync(int resultId, string? note);

    /// <summary>Manuel onayı geri alır — sonucu AI'nin ürettiği orijinal duruma döndürür.</summary>
    Task RevertOverrideAsync(int resultId);

    /// <summary>Yalnızca başarısız/manuel-kontrol sayfalarını yeniden dener.</summary>
    Task<AiAnalysisJobDto> RetryFailedPagesAsync(int jobId, IProgress<AiJobProgressUpdate>? progress, CancellationToken cancellationToken = default);
}
