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

    /// <summary>Kullanıcının manuel düzelttiği malzeme miktarını kaydeder (AI'nın orijinal değeri korunur).</summary>
    Task CorrectMaterialAsync(int materialId, decimal? correctedQuantity, string? correctedUnit, string? note);

    /// <summary>Belirsiz mağaza eşleşmesini kullanıcı elle onaylar/düzeltir.</summary>
    Task CorrectPageStoreAsync(int pageId, int storeId);

    /// <summary>Yalnızca başarısız/manuel-kontrol sayfalarını yeniden dener.</summary>
    Task<AiAnalysisJobDto> RetryFailedPagesAsync(int jobId, IProgress<AiJobProgressUpdate>? progress, CancellationToken cancellationToken = default);
}
