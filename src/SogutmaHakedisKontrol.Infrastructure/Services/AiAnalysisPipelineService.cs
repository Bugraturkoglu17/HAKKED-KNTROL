using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Belge Analiz Pipeline'ı: PDF → sayfa görselleri → GPT-5.5 yapılandırılmış çıkarım →
/// mağaza eşleştirmesi → deterministic iş kuralları (adam-saat, periyodik bakım çakışması) →
/// hakediş Excel karşılaştırması. AI yalnızca görsel okuma/anlamsal çıkarım yapar;
/// matematik ve iş kuralları burada, kod tarafında uygulanır.
/// Her sayfanın durumu ayrı kaydedilir; bir sayfanın hatası diğerlerini etkilemez.
/// </summary>
public class AiAnalysisPipelineService : IAiAnalysisPipelineService
{
    private const int MaxRetriesPerPage = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
    private const decimal ManHoursTolerance = 0.1m;
    private const decimal MaterialQuantityTolerance = 0.01m;

    private readonly AppDbContext _db;
    private readonly IAiVisionClient _visionClient;
    private readonly IPdfPageRasterizer _rasterizer;
    private readonly IManHoursCalculator _manHours;
    private readonly IAiUsageTracker _usageTracker;
    private readonly IAppPathService _appPath;
    private readonly ICategoryControlProfileRegistry _categoryProfiles;
    private readonly ICategoryComparisonStrategyRegistry _comparisonStrategies;

    private readonly int _maxConcurrency;

    public AiAnalysisPipelineService(
        AppDbContext db,
        IAiVisionClient visionClient,
        IPdfPageRasterizer rasterizer,
        IManHoursCalculator manHours,
        IAiUsageTracker usageTracker,
        IAppPathService appPath,
        ICategoryControlProfileRegistry categoryProfiles,
        ICategoryComparisonStrategyRegistry comparisonStrategies)
    {
        _db = db;
        _visionClient = visionClient;
        _rasterizer = rasterizer;
        _manHours = manHours;
        _usageTracker = usageTracker;
        _appPath = appPath;
        _categoryProfiles = categoryProfiles;
        _comparisonStrategies = comparisonStrategies;

        _maxConcurrency = int.TryParse(Environment.GetEnvironmentVariable("OPENAI_MAX_CONCURRENCY"), out var c) && c > 0 ? c : 3;
    }

    // ------------------------------------------------------------------ //
    //  ANA AKIŞ
    // ------------------------------------------------------------------ //
    public async Task<AiAnalysisJobDto> RunAsync(
        int progressPaymentCheckId,
        IReadOnlyList<(byte[] Bytes, string FileName)> serviceForms,
        byte[]? maintenanceFormsPdf, string? maintenanceFormsFileName,
        IProgress<AiJobProgressUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        var check = await _db.ProgressPaymentChecks.FindAsync(new object?[] { progressPaymentCheckId }, cancellationToken)
            ?? throw new InvalidOperationException("Hakediş kontrol kaydı bulunamadı.");

        if (!_visionClient.IsConfigured)
            throw new InvalidOperationException(
                "OpenAI API anahtarı yapılandırılmamış (OPENAI_API_KEY). Lütfen ortam değişkenini ayarlayıp uygulamayı yeniden başlatın.");

        Report(progress, AiJobStatus.Pending, "PDF hazırlanıyor...");

        var validServiceForms = (serviceForms ?? Array.Empty<(byte[], string)>())
            .Where(f => f.Bytes is { Length: > 0 }).ToList();

        var job = new AiAnalysisJob
        {
            ProgressPaymentCheckId = progressPaymentCheckId,
            ServiceFormsFileName = BuildServiceFormsDisplayName(validServiceForms),
            MaintenanceFormsFileName = maintenanceFormsFileName,
            Status = AiJobStatus.Splitting,
            CreatedAt = DateTime.Now,
        };
        _db.AiAnalysisJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);

        // ── Servis formu PDF'lerini sakla, sayfalara ayır, hangi sayfanın hangi dosyadan
        //    geldiğini AiSourceDocument (PageOffset/PageCount aralığı) ile kaydet ──────────
        var servicePages = new List<byte[]>();
        var sourceDocuments = new List<AiSourceDocument>();
        foreach (var (bytes, fileName) in validServiceForms)
        {
            var path = SavePdf(check, fileName, bytes);
            var pages = _rasterizer.RasterizeToPngPages(bytes);
            sourceDocuments.Add(new AiSourceDocument
            {
                JobId = job.Id,
                SourceKind = AiDocumentSource.ServiceForm,
                FileName = fileName,
                FilePath = path,
                PageCount = pages.Count,
                PageOffset = servicePages.Count,
                CreatedAt = DateTime.Now,
            });
            servicePages.AddRange(pages);
        }
        if (sourceDocuments.Count > 0)
        {
            _db.AiSourceDocuments.AddRange(sourceDocuments);
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (maintenanceFormsPdf is { Length: > 0 })
            job.MaintenanceFormsFilePath = SavePdf(check, maintenanceFormsFileName ?? "periyodik-bakim.pdf", maintenanceFormsPdf);

        var maintenancePages = maintenanceFormsPdf is { Length: > 0 } ? _rasterizer.RasterizeToPngPages(maintenanceFormsPdf) : new List<byte[]>();

        job.TotalServiceFormPages = servicePages.Count;
        job.TotalMaintenancePages = maintenancePages.Count;
        Report(progress, AiJobStatus.Splitting,
            $"{servicePages.Count + maintenancePages.Count} sayfa bulundu " +
            $"({servicePages.Count} servis formu, {maintenancePages.Count} bakım formu).");

        var pageEntities = new List<AiDocumentPage>();
        for (int i = 0; i < servicePages.Count; i++)
            pageEntities.Add(NewPage(job.Id, AiDocumentSource.ServiceForm, i + 1));
        for (int i = 0; i < maintenancePages.Count; i++)
            pageEntities.Add(NewPage(job.Id, AiDocumentSource.PeriodicMaintenance, i + 1));

        _db.AiDocumentPages.AddRange(pageEntities);
        await _db.SaveChangesAsync(cancellationToken);

        // ── Analiz: kontrollü eşzamanlılık + sayfa başına retry ─────────
        job.Status = AiJobStatus.Analyzing;
        await _db.SaveChangesAsync(cancellationToken);

        var serviceImageByPageId = pageEntities.Where(p => p.SourceKind == AiDocumentSource.ServiceForm)
            .Zip(servicePages, (p, img) => (p.Id, img)).ToDictionary(x => x.Id, x => x.img);
        var maintenanceImageByPageId = pageEntities.Where(p => p.SourceKind == AiDocumentSource.PeriodicMaintenance)
            .Zip(maintenancePages, (p, img) => (p.Id, img)).ToDictionary(x => x.Id, x => x.img);

        var categoryProfile = _categoryProfiles.Get(check.Category);
        await AnalyzePagesAsync(job, pageEntities.Where(p => p.SourceKind == AiDocumentSource.ServiceForm).ToList(),
            serviceImageByPageId, "Servis formları", progress, cancellationToken, categoryProfile.AiInstructionSupplement);
        await AnalyzePagesAsync(job, pageEntities.Where(p => p.SourceKind == AiDocumentSource.PeriodicMaintenance).ToList(),
            maintenanceImageByPageId, "Periyodik bakım formları", progress, cancellationToken, categoryProfile.AiInstructionSupplement);

        // ── Deterministic iş kuralları (adam-saat, periyodik bakım çakışması) ──
        job.Status = AiJobStatus.Matching;
        Report(progress, AiJobStatus.Matching, "Adam-saat hesapları yapılıyor...");
        await ApplyBusinessRulesAsync(job, cancellationToken);

        // ── Hakediş karşılaştırması ──────────────────────────────────────
        job.Status = AiJobStatus.Comparing;
        Report(progress, AiJobStatus.Comparing, "Malzeme ve servis ücreti kontrolleri yapılıyor...");
        await _db.SaveChangesAsync(cancellationToken);
        await _comparisonStrategies.Get(check.Category).BuildAsync(job, cancellationToken);
        Report(progress, AiJobStatus.Comparing, "Hakediş karşılaştırması tamamlanıyor...");

        // ── Tamamlandı ────────────────────────────────────────────────────
        var refreshedPages = await _db.AiDocumentPages.Where(p => p.JobId == job.Id).ToListAsync(cancellationToken);
        job.FailedPages = refreshedPages.Count(p => p.Status == AiPageStatus.Failed);
        job.ManualReviewPages = refreshedPages.Count(p => p.RequiresManualReview);
        job.ProcessedPages = refreshedPages.Count(p => p.Status is AiPageStatus.Succeeded or AiPageStatus.ManualReview);
        job.Status = job.FailedPages > 0 ? AiJobStatus.CompletedWithErrors : AiJobStatus.Completed;
        job.CompletedAt = DateTime.Now;
        Report(progress, job.Status, "Kontrol tamamlandı.");
        await _db.SaveChangesAsync(cancellationToken);

        return await BuildJobDtoAsync(job.Id) ?? throw new InvalidOperationException("Job oluşturulamadı.");
    }

    private static AiDocumentPage NewPage(int jobId, AiDocumentSource source, int pageNumber) => new()
    {
        JobId = jobId,
        SourceKind = source,
        PageNumber = pageNumber,
        Status = AiPageStatus.Pending,
        CreatedAt = DateTime.Now,
    };

    private static string? BuildServiceFormsDisplayName(List<(byte[] Bytes, string FileName)> files) => files.Count switch
    {
        0 => null,
        1 => files[0].FileName,
        _ => $"{files.Count} dosya ({string.Join(", ", files.Select(f => f.FileName))})",
    };

    private string SavePdf(ProgressPaymentCheck check, string fileName, byte[] bytes)
    {
        var folder = Path.Combine(_appPath.DataRootPath, "SogutmaHakedisKontrol", check.Year.ToString(), $"{check.Month:00}", "AiBelgeleri");
        Directory.CreateDirectory(folder);
        var safeName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ------------------------------------------------------------------ //
    //  ANALİZ — kontrollü eşzamanlılık + retry
    // ------------------------------------------------------------------ //
    private async Task AnalyzePagesAsync(
        AiAnalysisJob job, List<AiDocumentPage> pages, Dictionary<int, byte[]> imagesByPageId,
        string label, IProgress<AiJobProgressUpdate>? progress, CancellationToken cancellationToken, string? extraInstruction = null)
    {
        if (pages.Count == 0) return;

        using var semaphore = new SemaphoreSlim(_maxConcurrency);
        int completed = 0;
        var tasks = pages.Select(async page =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await AnalyzeSinglePageWithRetryAsync(job.Id, page, imagesByPageId[page.Id], extraInstruction, cancellationToken);
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                Report(progress, AiJobStatus.Analyzing, $"{label} analiz ediliyor: {done} / {pages.Count}", done, pages.Count);
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task AnalyzeSinglePageWithRetryAsync(int jobId, AiDocumentPage page, byte[] imageBytes, string? extraInstruction, CancellationToken cancellationToken)
    {
        // Not: EF Core DbContext eşzamanlı erişime uygun değildir — her sayfa kendi scope'unda,
        // yalnızca kendi satırını günceller ve hemen kaydeder (satır bazlı bağımsız kayıt).
        AiVisionCallResultDto? result = null;
        string? lastError = null;

        for (int attempt = 1; attempt <= MaxRetriesPerPage; attempt++)
        {
            try
            {
                result = await _visionClient.AnalyzePageAsync(imageBytes, extraInstruction, cancellationToken);
                if (result.Success) break;
                lastError = result.ErrorMessage;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            if (attempt < MaxRetriesPerPage)
                await Task.Delay(RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)], cancellationToken);
        }

        lock (_db)
        {
            ApplyPageResult(page, result, lastError);
            _db.SaveChanges();
            if (result?.Usage != null)
                _db.AiUsageLogs.Add(new AiUsageLog
                {
                    JobId = jobId,
                    PageId = page.Id,
                    Model = result.Usage.Model,
                    InputTokens = result.Usage.InputTokens,
                    CachedInputTokens = result.Usage.CachedInputTokens,
                    OutputTokens = result.Usage.OutputTokens,
                    ReasoningTokens = result.Usage.ReasoningTokens,
                    RequestedAt = DateTime.Now,
                });
            _db.SaveChanges();
        }
    }

    private void ApplyPageResult(AiDocumentPage page, AiVisionCallResultDto? result, string? lastError)
    {
        page.ProcessedAt = DateTime.Now;
        page.RetryCount = MaxRetriesPerPage;

        if (result is null || !result.Success || result.Extraction is null)
        {
            page.Status = AiPageStatus.Failed;
            page.ErrorMessage = lastError ?? "Bilinmeyen hata";
            return;
        }

        var x = result.Extraction;
        page.RawResponseJson = result.RawJson;
        page.DocumentType = x.DocumentType?.ToUpperInvariant() switch
        {
            "SERVICE_FORM" => AiDocumentType.ServiceForm,
            "PERIODIC_MAINTENANCE_FORM" => AiDocumentType.PeriodicMaintenanceForm,
            "SUMMARY" => AiDocumentType.Summary,
            _ => AiDocumentType.Unknown,
        };
        page.FormNumber = x.FormNumber;
        page.FormNumberConfidence = x.FormNumberConfidence;
        page.StoreCodeRaw = x.Store?.CodeRaw;
        page.StoreNameRaw = x.Store?.NameRaw;
        page.StoreConfidence = x.Store?.Confidence;
        page.ServiceDate = TryParseDate(x.ServiceDate);
        page.MaintenanceDate = TryParseDate(x.MaintenanceDate);
        page.DescriptionRaw = x.DescriptionRaw;
        page.WorkPerformedRaw = x.WorkPerformedRaw;
        page.FormTotalHoursRaw = x.FormTotalHours;
        page.RequiresManualReview = x.RequiresManualReview;
        page.ManualReviewReason = x.Warnings is { Count: > 0 } ? string.Join(" ", x.Warnings) : null;

        foreach (var emp in x.Employees)
        {
            var start = TryParseTime(emp.StartTime);
            var end = TryParseTime(emp.EndTime);
            page.Employees.Add(new AiPageEmployee
            {
                NameRaw = emp.NameRaw,
                StartTimeRaw = emp.StartTime,
                EndTimeRaw = emp.EndTime,
                StartTime = start,
                EndTime = end,
                HoursWorked = _manHours.CalculateHours(start, end),
                Confidence = emp.Confidence,
            });
        }

        foreach (var mat in x.Materials)
        {
            page.Materials.Add(new AiPageMaterial
            {
                RawName = mat.RawName,
                NormalizedName = mat.NormalizedName,
                Quantity = mat.Quantity,
                Unit = mat.Unit,
                Confidence = mat.Confidence,
                RequiresManualReview = mat.RequiresManualReview,
            });
        }

        if (page.DocumentType == AiDocumentType.Unknown)
        {
            page.RequiresManualReview = true;
            if (string.IsNullOrEmpty(page.ManualReviewReason))
                page.ManualReviewReason = "Belge türü sınıflandırılamadı.";
        }
        page.Status = page.RequiresManualReview ? AiPageStatus.ManualReview : AiPageStatus.Succeeded;
    }

    private static DateTime? TryParseDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;

    private static TimeSpan? TryParseTime(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && TimeSpan.TryParse(raw.Replace('.', ':'), CultureInfo.InvariantCulture, out var t) ? t : null;


    // ------------------------------------------------------------------ //
    //  DETERMİNİSTİK İŞ KURALLARI
    // ------------------------------------------------------------------ //
    private async Task ApplyBusinessRulesAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == job.Id)
            .Include(p => p.Employees)
            .ToListAsync(cancellationToken);

        // Adam-saat: yalnızca SERVICE_FORM sayfaları için
        foreach (var page in pages.Where(p => p.DocumentType == AiDocumentType.ServiceForm))
        {
            var total = page.Employees.Where(e => e.HoursWorked.HasValue).Sum(e => e.HoursWorked!.Value);
            page.CalculatedManHours = total;
            page.PayableManHours = _manHours.CalculatePayableHours(total);
            page.FormTotalMatch = page.FormTotalHoursRaw.HasValue
                ? Math.Abs(page.FormTotalHoursRaw.Value - total) <= ManHoursTolerance
                : null;
        }

        // Periyodik bakım çakışması: aynı mağaza + aynı tarihte bakım varsa şehiriçi/şehirdışı servis ücreti reddedilir.
        // Mağaza kimliği artık dış mağaza ana listesine değil, formdan okunan ham mağaza koduna/adına dayanır
        // (bu iş akışında ayrı mağaza listesi kullanılmaz — bkz. form numarası bazlı eşleştirme).
        var maintenanceDates = pages
            .Where(p => p.DocumentType == AiDocumentType.PeriodicMaintenanceForm && p.MaintenanceDate.HasValue)
            .Select(p => (Store: RawStoreKey(p), Date: p.MaintenanceDate!.Value.Date))
            .Where(x => !string.IsNullOrEmpty(x.Store))
            .ToHashSet();

        foreach (var page in pages.Where(p => p.DocumentType == AiDocumentType.ServiceForm))
        {
            var storeKey = RawStoreKey(page);
            if (!string.IsNullOrEmpty(storeKey) && page.ServiceDate.HasValue &&
                maintenanceDates.Contains((storeKey, page.ServiceDate.Value.Date)))
            {
                page.ServiceFeeRejectedDueToMaintenance = true;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Dış mağaza ana listesi kullanılmadığı için sayfadaki ham mağaza kodu/adını normalize ederek
    /// mağaza kimliği olarak kullanır — kod varsa öncelikli, yoksa ada düşer.</summary>
    private static string RawStoreKey(AiDocumentPage p)
    {
        var code = TextNormalizationHelper.NormalizeCode(p.StoreCodeRaw ?? string.Empty);
        if (!string.IsNullOrEmpty(code)) return code;
        return TextNormalizationHelper.NormalizeName(p.StoreNameRaw ?? string.Empty);
    }

    // ------------------------------------------------------------------ //
    //  HAKEDİŞ KARŞILAŞTIRMASI — bkz. ICategoryComparisonStrategy (CategoryComparisonStrategies.cs)
    //  DefaultCategoryComparisonStrategy / GasUsageComparisonStrategy, _comparisonStrategies üzerinden çağrılır.
    // ------------------------------------------------------------------ //
    private static void Report(IProgress<AiJobProgressUpdate>? progress, AiJobStatus status, string message, int? current = null, int? total = null)
        => progress?.Report(new AiJobProgressUpdate { Status = status, Message = message, Current = current, Total = total });

    // ------------------------------------------------------------------ //
    //  OKUMA
    // ------------------------------------------------------------------ //
    public async Task<AiAnalysisJobDto?> GetJobAsync(int jobId) => await BuildJobDtoAsync(jobId);

    public async Task<AiAnalysisJobDto?> GetLatestJobForCheckAsync(int progressPaymentCheckId)
    {
        var job = await _db.AiAnalysisJobs
            .Where(j => j.ProgressPaymentCheckId == progressPaymentCheckId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();
        return job is null ? null : await BuildJobDtoAsync(job.Id);
    }

    private async Task<AiAnalysisJobDto?> BuildJobDtoAsync(int jobId)
    {
        var job = await _db.AiAnalysisJobs.FindAsync(jobId);
        if (job is null) return null;

        var pages = await _db.AiDocumentPages.Where(p => p.JobId == jobId).ToListAsync();
        var results = await _db.AiComparisonResults.Where(r => r.JobId == jobId).ToListAsync();

        return new AiAnalysisJobDto
        {
            Id = job.Id,
            ProgressPaymentCheckId = job.ProgressPaymentCheckId,
            ServiceFormsFileName = job.ServiceFormsFileName,
            MaintenanceFormsFileName = job.MaintenanceFormsFileName,
            Status = job.Status,
            CurrentStepDescription = job.CurrentStepDescription,
            TotalServiceFormPages = job.TotalServiceFormPages,
            TotalMaintenancePages = job.TotalMaintenancePages,
            ProcessedPages = job.ProcessedPages,
            FailedPages = job.FailedPages,
            ManualReviewPages = job.ManualReviewPages,
            ErrorMessage = job.ErrorMessage,
            CreatedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
            MatchedStoreCount = pages.Where(p => p.MatchedStoreId.HasValue).Select(p => p.MatchedStoreId).Distinct().Count(),
            ManualReviewDocumentCount = pages.Count(p => p.RequiresManualReview),
            SummaryPageCount = pages.Count(p => p.DocumentType == AiDocumentType.Summary),
            ClassifiedServiceFormPageCount = pages.Count(p => p.DocumentType == AiDocumentType.ServiceForm),
            ClassifiedMaintenancePageCount = pages.Count(p => p.DocumentType == AiDocumentType.PeriodicMaintenanceForm),
            UnknownPageCount = pages.Count(p => p.DocumentType == AiDocumentType.Unknown && p.Status != AiPageStatus.Failed),
            UygunItemCount = results.Count(r => r.Status == AiComparisonStatus.Uygun),
            UygunDegilItemCount = results.Count(r => r.Status == AiComparisonStatus.UygunDegil),
            EksikItemCount = results.Count(r => r.Status == AiComparisonStatus.Eksik),
            FazlaItemCount = results.Count(r => r.Status == AiComparisonStatus.Fazla),
            RejectedServiceFeeCount = results.Count(r => r.ItemType == AiComparisonItemType.ServiceFee && r.Status == AiComparisonStatus.UygunDegil),
            ManHoursDiscrepancyCount = results.Count(r => r.ItemType == AiComparisonItemType.ManHours && r.Status == AiComparisonStatus.UygunDegil),
        };
    }

    public async Task<List<AiDocumentPageDto>> GetPagesAsync(int jobId)
    {
        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == jobId)
            .Include(p => p.Employees).Include(p => p.Materials)
            .OrderBy(p => p.SourceKind).ThenBy(p => p.PageNumber)
            .ToListAsync();

        var storeIds = pages.Where(p => p.MatchedStoreId.HasValue).Select(p => p.MatchedStoreId!.Value).Distinct().ToList();
        var stores = await _db.Stores.Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

        var job = await _db.AiAnalysisJobs.FindAsync(jobId);
        var serviceSourceDocs = await _db.AiSourceDocuments
            .Where(d => d.JobId == jobId && d.SourceKind == AiDocumentSource.ServiceForm)
            .ToListAsync();
        string? ResolveSourceFileName(AiDocumentPage p) => p.SourceKind switch
        {
            AiDocumentSource.ServiceForm => serviceSourceDocs
                .FirstOrDefault(d => p.PageNumber > d.PageOffset && p.PageNumber <= d.PageOffset + d.PageCount)?.FileName,
            AiDocumentSource.PeriodicMaintenance => job?.MaintenanceFormsFileName,
            _ => null,
        };

        return pages.Select(p => new AiDocumentPageDto
        {
            Id = p.Id,
            JobId = p.JobId,
            SourceKind = p.SourceKind.ToString(),
            PageNumber = p.PageNumber,
            SourceFileName = ResolveSourceFileName(p),
            Status = p.Status.ToString(),
            DocumentType = p.DocumentType.ToString(),
            StoreCodeRaw = p.StoreCodeRaw,
            StoreNameRaw = p.StoreNameRaw,
            StoreConfidence = p.StoreConfidence,
            MatchedStoreId = p.MatchedStoreId,
            MatchedStoreLabel = p.MatchedStoreId.HasValue && stores.TryGetValue(p.MatchedStoreId.Value, out var s) ? $"{s.Code} — {s.Name}" : null,
            StoreMatchMethod = p.StoreMatchMethod.ToString(),
            FormNumber = p.FormNumber,
            FormNumberConfidence = p.FormNumberConfidence,
            ServiceDate = p.ServiceDate,
            MaintenanceDate = p.MaintenanceDate,
            DescriptionRaw = p.DescriptionRaw,
            WorkPerformedRaw = p.WorkPerformedRaw,
            FormTotalHoursRaw = p.FormTotalHoursRaw,
            CalculatedManHours = p.CalculatedManHours,
            PayableManHours = p.PayableManHours,
            FormTotalMatch = p.FormTotalMatch,
            ServiceFeeRejectedDueToMaintenance = p.ServiceFeeRejectedDueToMaintenance,
            ErrorMessage = p.ErrorMessage,
            RequiresManualReview = p.RequiresManualReview,
            ManualReviewReason = p.ManualReviewReason,
            Employees = p.Employees.Select(e => new AiPageEmployeeDto
            {
                Id = e.Id, NameRaw = e.NameRaw, StartTimeRaw = e.StartTimeRaw, EndTimeRaw = e.EndTimeRaw,
                HoursWorked = e.HoursWorked, Confidence = e.Confidence,
            }).ToList(),
            Materials = p.Materials.Select(m => new AiPageMaterialDto
            {
                Id = m.Id, RawName = m.RawName, NormalizedName = m.NormalizedName, Quantity = m.Quantity,
                Unit = m.Unit, Confidence = m.Confidence, RequiresManualReview = m.RequiresManualReview,
                UserCorrectedQuantity = m.UserCorrectedQuantity, UserCorrectedUnit = m.UserCorrectedUnit,
            }).ToList(),
        }).ToList();
    }

    public async Task<List<AiComparisonResultDto>> GetComparisonResultsAsync(int jobId)
    {
        var results = await _db.AiComparisonResults
            .Where(r => r.JobId == jobId)
            .OrderBy(r => r.StoreLabel).ThenBy(r => r.VisitDate)
            .ToListAsync();

        var pageIds = results.Where(r => r.SourcePageId.HasValue).Select(r => r.SourcePageId!.Value).Distinct().ToList();
        var pages = await _db.AiDocumentPages.Where(p => pageIds.Contains(p.Id)).ToListAsync();
        var job = await _db.AiAnalysisJobs.FindAsync(jobId);
        var serviceSourceDocs = await _db.AiSourceDocuments
            .Where(d => d.JobId == jobId && d.SourceKind == AiDocumentSource.ServiceForm)
            .ToListAsync();

        string? FormReference(AiComparisonResult r)
        {
            var page = pages.FirstOrDefault(p => p.Id == r.SourcePageId);
            if (page is null) return null;
            if (!string.IsNullOrWhiteSpace(page.FormNumber)) return page.FormNumber; // eşleştirmenin ana anahtarı — önce bu gösterilir
            var fileName = page.SourceKind == AiDocumentSource.ServiceForm
                ? serviceSourceDocs.FirstOrDefault(d => page.PageNumber > d.PageOffset && page.PageNumber <= d.PageOffset + d.PageCount)?.FileName
                : job?.MaintenanceFormsFileName;
            return $"{fileName ?? "Belge"} · Sayfa {page.PageNumber}";
        }

        return results.Select(r => new AiComparisonResultDto
        {
            Id = r.Id,
            JobId = r.JobId,
            StoreLabel = r.StoreLabel,
            VisitDate = r.VisitDate,
            FormReference = FormReference(r),
            ItemType = r.ItemType.ToString(),
            Description = r.Description,
            FormValue = r.FormValue,
            HakedisValue = r.HakedisValue,
            Status = r.Status.ToString(),
            Explanation = r.Explanation,
        }).ToList();
    }

    // ------------------------------------------------------------------ //
    //  MANUEL DÜZELTME
    // ------------------------------------------------------------------ //
    public async Task CorrectMaterialAsync(int materialId, decimal? correctedQuantity, string? correctedUnit, string? note)
    {
        var material = await _db.AiPageMaterials.Include(m => m.Page).FirstOrDefaultAsync(m => m.Id == materialId)
            ?? throw new InvalidOperationException("Malzeme kaydı bulunamadı.");

        material.UserCorrectedQuantity = correctedQuantity;
        material.UserCorrectedUnit = correctedUnit;
        material.UserCorrectedAt = DateTime.Now;
        material.CorrectionNote = note;
        await _db.SaveChangesAsync();

        await RecomputeComparisonForJobAsync(material.Page.JobId);
    }

    public async Task CorrectPageStoreAsync(int pageId, int storeId)
    {
        var page = await _db.AiDocumentPages.FindAsync(pageId)
            ?? throw new InvalidOperationException("Sayfa kaydı bulunamadı.");

        page.MatchedStoreId = storeId;
        page.StoreMatchMethod = StoreMatchMethod.AiSuggestedConfirmed;
        page.RequiresManualReview = false;
        page.ManualReviewReason = null;
        if (page.Status == AiPageStatus.ManualReview) page.Status = AiPageStatus.Succeeded;
        await _db.SaveChangesAsync();

        await RecomputeComparisonForJobAsync(page.JobId);
    }

    private async Task RecomputeComparisonForJobAsync(int jobId)
    {
        var job = await _db.AiAnalysisJobs.FindAsync(jobId);
        var check = job is null ? null : await _db.ProgressPaymentChecks.FindAsync(job.ProgressPaymentCheckId);
        if (job is null || check is null) return;
        await _comparisonStrategies.Get(check.Category).BuildAsync(job, CancellationToken.None);
    }

    // ------------------------------------------------------------------ //
    //  BAŞARISIZ SAYFALARI YENİDEN DENE
    // ------------------------------------------------------------------ //
    public async Task<AiAnalysisJobDto> RetryFailedPagesAsync(int jobId, IProgress<AiJobProgressUpdate>? progress, CancellationToken cancellationToken = default)
    {
        var job = await _db.AiAnalysisJobs.FindAsync(jobId) ?? throw new InvalidOperationException("Job bulunamadı.");
        var check = await _db.ProgressPaymentChecks.FindAsync(job.ProgressPaymentCheckId);
        var extraInstruction = _categoryProfiles.Get(check?.Category).AiInstructionSupplement;
        var failedPages = await _db.AiDocumentPages
            .Where(p => p.JobId == jobId && p.Status == AiPageStatus.Failed)
            .ToListAsync(cancellationToken);

        if (failedPages.Count == 0) return await BuildJobDtoAsync(jobId) ?? throw new InvalidOperationException();

        Report(progress, AiJobStatus.Analyzing, $"{failedPages.Count} başarısız sayfa yeniden deneniyor...");

        var byServiceForm = failedPages.Where(p => p.SourceKind == AiDocumentSource.ServiceForm).ToList();
        var byMaintenance = failedPages.Where(p => p.SourceKind == AiDocumentSource.PeriodicMaintenance).ToList();

        if (byServiceForm.Count > 0)
        {
            var sourceDocs = await _db.AiSourceDocuments
                .Where(d => d.JobId == jobId && d.SourceKind == AiDocumentSource.ServiceForm)
                .OrderBy(d => d.PageOffset)
                .ToListAsync(cancellationToken);

            foreach (var doc in sourceDocs)
            {
                var pagesInDoc = byServiceForm
                    .Where(p => p.PageNumber > doc.PageOffset && p.PageNumber <= doc.PageOffset + doc.PageCount)
                    .ToList();
                if (pagesInDoc.Count == 0 || !File.Exists(doc.FilePath)) continue;

                var allPages = _rasterizer.RasterizeToPngPages(await File.ReadAllBytesAsync(doc.FilePath, cancellationToken));
                var images = pagesInDoc.ToDictionary(p => p.Id, p => allPages[p.PageNumber - doc.PageOffset - 1]);
                await AnalyzePagesAsync(job, pagesInDoc, images, $"Servis formları (retry) — {doc.FileName}", progress, cancellationToken, extraInstruction);
            }
        }
        if (byMaintenance.Count > 0 && job.MaintenanceFormsFilePath != null && File.Exists(job.MaintenanceFormsFilePath))
        {
            var allPages = _rasterizer.RasterizeToPngPages(await File.ReadAllBytesAsync(job.MaintenanceFormsFilePath, cancellationToken));
            var images = byMaintenance.ToDictionary(p => p.Id, p => allPages[p.PageNumber - 1]);
            await AnalyzePagesAsync(job, byMaintenance, images, "Periyodik bakım formları (retry)", progress, cancellationToken, extraInstruction);
        }

        if (check != null)
        {
            await ApplyBusinessRulesAsync(job, cancellationToken);
            await _comparisonStrategies.Get(check.Category).BuildAsync(job, cancellationToken);
        }

        var refreshedPages = await _db.AiDocumentPages.Where(p => p.JobId == job.Id).ToListAsync(cancellationToken);
        job.FailedPages = refreshedPages.Count(p => p.Status == AiPageStatus.Failed);
        job.ManualReviewPages = refreshedPages.Count(p => p.RequiresManualReview);
        job.ProcessedPages = refreshedPages.Count(p => p.Status is AiPageStatus.Succeeded or AiPageStatus.ManualReview);
        job.Status = job.FailedPages > 0 ? AiJobStatus.CompletedWithErrors : AiJobStatus.Completed;
        await _db.SaveChangesAsync(cancellationToken);

        return await BuildJobDtoAsync(jobId) ?? throw new InvalidOperationException();
    }
}
