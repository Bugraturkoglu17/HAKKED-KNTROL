using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

internal static class ComparisonResultFactory
{
    public static AiComparisonResult New(int jobId, AiDocumentPage page, string storeLabel,
        AiComparisonItemType type, string description, string? formValue, string? hakedisValue,
        AiComparisonStatus status, string explanation, int? checkItemId = null) => new()
    {
        JobId = jobId,
        StoreId = page.MatchedStoreId,
        StoreLabel = storeLabel,
        VisitDate = page.ServiceDate,
        SourcePageId = page.Id,
        ProgressPaymentCheckItemId = checkItemId,
        ItemType = type,
        Description = description,
        FormValue = formValue,
        HakedisValue = hakedisValue,
        Status = status,
        Explanation = explanation,
        CreatedAt = DateTime.Now,
    };
}

/// <summary>
/// Genel/kategori-bağımsız karşılaştırma — malzeme fuzzy eşleşmesi, adam-saat ve servis ücreti
/// kontrolü. Kategori bazlı özel strateji tanımlanmamış her hakediş türü bunu kullanır
/// (Kompresör, Glikol, Evap, Kısmi Tadilat, İzleme, Periyodik Bakım, İlave İşler, kategori seçilmemiş eski kayıtlar).
/// Bu mantık, önceki tek-parça AiAnalysisPipelineService.BuildComparisonAsync'ten aynen taşınmıştır.
/// </summary>
public class DefaultCategoryComparisonStrategy : ICategoryComparisonStrategy
{
    private const decimal ManHoursTolerance = 0.1m;
    private const decimal MaterialQuantityTolerance = 0.01m;

    private readonly AppDbContext _db;
    public DefaultCategoryComparisonStrategy(AppDbContext db) => _db = db;

    public HakedisCategory? Category => null;

    public async Task BuildAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AiComparisonResults.Where(r => r.JobId == job.Id).ToListAsync(cancellationToken);
        _db.AiComparisonResults.RemoveRange(existing);

        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == job.Id && p.DocumentType == AiDocumentType.ServiceForm && p.MatchedStoreId.HasValue)
            .Include(p => p.Materials)
            .ToListAsync(cancellationToken);

        var storeIds = pages.Where(p => p.MatchedStoreId.HasValue).Select(p => p.MatchedStoreId!.Value).Distinct().ToList();
        var stores = await _db.Stores.Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);

        var checkItems = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        var results = new List<AiComparisonResult>();

        foreach (var page in pages)
        {
            var storeLabel = page.MatchedStoreId.HasValue && stores.TryGetValue(page.MatchedStoreId.Value, out var s)
                ? $"{s.Code} — {s.Name}" : (page.StoreNameRaw ?? page.StoreCodeRaw ?? "Bilinmeyen Mağaza");

            var sameVisit = checkItems.Where(i =>
                i.MatchedStoreId == page.MatchedStoreId &&
                page.ServiceDate.HasValue && i.VisitDate.HasValue && i.VisitDate.Value.Date == page.ServiceDate.Value.Date)
                .ToList();

            // ── Malzeme: form → hakediş (eşleşme / uygun değil / eksik) ──
            var matchedHakedisMaterialIds = new HashSet<int>();
            foreach (var mat in page.Materials)
            {
                var effectiveQty = mat.UserCorrectedQuantity ?? mat.Quantity;
                var searchName = TextNormalizationHelper.NormalizeName(mat.NormalizedName ?? mat.RawName);

                var candidate = sameVisit
                    .Where(i => !i.IsServiceItem)
                    .Select(i => (Item: i, Score: TextNormalizationHelper.SimilarityRatio(searchName, TextNormalizationHelper.NormalizeName(i.OriginalMaterialName))))
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (candidate.Item != null && candidate.Score >= 0.6)
                {
                    matchedHakedisMaterialIds.Add(candidate.Item.Id);
                    var hakedisQty = candidate.Item.Quantity;
                    var formStr = effectiveQty.HasValue ? $"{effectiveQty.Value:0.##} {mat.UserCorrectedUnit ?? mat.Unit}" : "Okunamadı";
                    var hakedisStr = $"{hakedisQty:0.##} {candidate.Item.Unit}";

                    if (!effectiveQty.HasValue || mat.RequiresManualReview)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, mat.NormalizedName ?? mat.RawName,
                            formStr, hakedisStr, AiComparisonStatus.ManuelKontrol,
                            $"\"{mat.RawName}\" için okunan miktar belirsiz — manuel kontrol edilmeli.", candidate.Item.Id));
                    }
                    else if (Math.Abs(effectiveQty.Value - hakedisQty) <= MaterialQuantityTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, mat.NormalizedName ?? mat.RawName,
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Servis formu ile hakediş miktarı uyumlu.", candidate.Item.Id));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, mat.NormalizedName ?? mat.RawName,
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Servis formunda {formStr}, hakedişte {hakedisStr} girilmiş.", candidate.Item.Id));
                    }
                }
                else
                {
                    var formStr = effectiveQty.HasValue ? $"{effectiveQty.Value:0.##} {mat.UserCorrectedUnit ?? mat.Unit}" : "Okunamadı";
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, mat.NormalizedName ?? mat.RawName,
                        formStr, "—", AiComparisonStatus.Eksik,
                        $"Servis formunda \"{mat.RawName}\" bulunuyor ancak hakedişte bu kaleme rastlanmadı."));
                }
            }

            // ── Malzeme: hakediş → form (fazla / formda bulunamadı) ──────
            foreach (var item in sameVisit.Where(i => !i.IsServiceItem && !matchedHakedisMaterialIds.Contains(i.Id)))
            {
                results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                    "—", $"{item.Quantity:0.##} {item.Unit}", AiComparisonStatus.Fazla,
                    $"Hakedişte \"{item.OriginalMaterialName}\" bulunuyor fakat servis formunda bulunamadı.", item.Id));
            }

            // ── Adam-saat ──────────────────────────────────────────────
            if (page.PayableManHours.HasValue)
            {
                var hakedisManHours = sameVisit
                    .Where(i => i.IsServiceItem && TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("adamsaat"))
                    .Sum(i => i.Quantity);

                var formStr = $"{page.PayableManHours.Value:0.##} saat";
                var hakedisStr = $"{hakedisManHours:0.##} saat";
                if (Math.Abs(page.PayableManHours.Value - hakedisManHours) <= ManHoursTolerance)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                        formStr, hakedisStr, AiComparisonStatus.Uygun, "Formdaki çalışma sürelerine göre hesaplanan adam-saat hakedişle uyumlu."));
                }
                else
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                        formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                        $"Formdaki çalışma sürelerine göre toplam {page.CalculatedManHours:0.##} adam-saat oluşmaktadır. " +
                        $"Kural gereği 4 saat düşülerek en fazla {page.PayableManHours:0.##} adam-saat ödenebilir."));
                }
            }

            // ── Servis ücreti (şehiriçi/şehirdışı) ───────────────────────
            var serviceFeeItems = sameVisit.Where(i => i.IsServiceItem &&
                (TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("sehirici") ||
                 TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("sehirdisi"))).ToList();

            if (page.ServiceFeeRejectedDueToMaintenance)
            {
                foreach (var fee in serviceFeeItems)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                        "Periyodik bakım mevcut", $"{fee.Quantity:0.##} adet", AiComparisonStatus.UygunDegil,
                        $"{page.ServiceDate:dd.MM.yyyy} tarihinde bu mağazada periyodik bakım bulunmaktadır. " +
                        "Aynı tarih için ayrıca şehir içi/şehir dışı servis ücreti ödenemez.", fee.Id));
                }
            }
            else if (serviceFeeItems.Count > 0)
            {
                foreach (var fee in serviceFeeItems)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                        "Servis ziyareti mevcut", $"{fee.Quantity:0.##} adet", AiComparisonStatus.Uygun,
                        "Periyodik bakım çakışması yok, servis ücreti uygundur."));
                }
            }
        }

        _db.AiComparisonResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// GAZ KULLANIM HAKEDİŞİ için özel karşılaştırma: hakedişteki gaz kg'ı servis formundan çıkarılan
/// gaz kg'ı ile karşılaştırılır. Kaçak yeri/tespiti/onarımı gibi bilgiler (varsa) manuel kontrol
/// notuna eklenir — bu alanlar için henüz sayısal bir kural yok (iskelet aşaması).
/// </summary>
public class GasUsageComparisonStrategy : ICategoryComparisonStrategy
{
    private const decimal GasQuantityTolerance = 0.01m;
    private static readonly Regex GasKgRegex = new(@"(\d+(?:[.,]\d+)?)\s*kg", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] LeakKeywords = { "kacak" }; // TextNormalizationHelper "kaçak"→"kacak" çevirir

    private readonly AppDbContext _db;
    public GasUsageComparisonStrategy(AppDbContext db) => _db = db;

    public HakedisCategory? Category => HakedisCategory.GasUsage;

    public async Task BuildAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AiComparisonResults.Where(r => r.JobId == job.Id).ToListAsync(cancellationToken);
        _db.AiComparisonResults.RemoveRange(existing);

        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == job.Id && p.DocumentType == AiDocumentType.ServiceForm && p.MatchedStoreId.HasValue)
            .Include(p => p.Materials)
            .ToListAsync(cancellationToken);

        var storeIds = pages.Where(p => p.MatchedStoreId.HasValue).Select(p => p.MatchedStoreId!.Value).Distinct().ToList();
        var stores = await _db.Stores.Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);

        var checkItems = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        var results = new List<AiComparisonResult>();

        foreach (var page in pages)
        {
            var storeLabel = page.MatchedStoreId.HasValue && stores.TryGetValue(page.MatchedStoreId.Value, out var s)
                ? $"{s.Code} — {s.Name}" : (page.StoreNameRaw ?? page.StoreCodeRaw ?? "Bilinmeyen Mağaza");

            var sameVisit = checkItems.Where(i =>
                i.MatchedStoreId == page.MatchedStoreId &&
                page.ServiceDate.HasValue && i.VisitDate.HasValue && i.VisitDate.Value.Date == page.ServiceDate.Value.Date)
                .ToList();

            var formGasKg = ExtractGasKg(page);
            var hakedisGasItems = sameVisit.Where(i => TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("gaz")).ToList();
            var hakedisGasKg = hakedisGasItems.Sum(i => i.Quantity);
            var firstGasItemId = hakedisGasItems.FirstOrDefault()?.Id;

            if (hakedisGasItems.Count > 0 || formGasKg.HasValue)
            {
                if (!formGasKg.HasValue)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                        "Okunamadı", $"{hakedisGasKg:0.##} kg", AiComparisonStatus.ManuelKontrol,
                        "Hakedişte gaz kalemi var ancak servis formunda gaz kg bilgisi açıkça bulunamadı — manuel kontrol edilmeli.", firstGasItemId));
                }
                else
                {
                    var formStr = $"{formGasKg.Value:0.##} kg";
                    var hakedisStr = $"{hakedisGasKg:0.##} kg";
                    if (hakedisGasItems.Count == 0)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                            formStr, "—", AiComparisonStatus.Fazla,
                            $"Servis formunda {formStr} gaz kullanımı belirtilmiş ancak hakedişte gaz kalemi bulunamadı."));
                    }
                    else if (Math.Abs(formGasKg.Value - hakedisGasKg) <= GasQuantityTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Hakedişteki gaz miktarı servis formuyla uyumlu.", firstGasItemId));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Hakedişte {hakedisStr} gaz belirtilmiş, servis formunda {formStr} tespit edilmiştir.", firstGasItemId));
                    }
                }
            }

            // Kaçak bilgisi — henüz sayısal bir kural yok; bilgi amaçlı manuel kontrol notu (iskelet aşaması).
            if (!string.IsNullOrWhiteSpace(page.DescriptionRaw) &&
                LeakKeywords.Any(k => TextNormalizationHelper.NormalizeName(page.DescriptionRaw).Contains(k)))
            {
                var snippet = page.DescriptionRaw.Length > 200 ? page.DescriptionRaw[..200] + "…" : page.DescriptionRaw;
                results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Kaçak Bilgisi",
                    null, null, AiComparisonStatus.ManuelKontrol,
                    $"Servis formunda kaçak ile ilgili bilgi bulunuyor: \"{snippet}\" — kaçak yeri/tespiti/onarımı manuel doğrulanmalıdır."));
            }
        }

        _db.AiComparisonResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static decimal? ExtractGasKg(AiDocumentPage page)
    {
        var gasMaterial = page.Materials.FirstOrDefault(m =>
            TextNormalizationHelper.NormalizeName(m.RawName ?? m.NormalizedName ?? string.Empty).Contains("gaz"));
        if (gasMaterial != null)
            return gasMaterial.UserCorrectedQuantity ?? gasMaterial.Quantity;

        if (!string.IsNullOrEmpty(page.DescriptionRaw))
        {
            var match = GasKgRegex.Match(page.DescriptionRaw);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return v;
        }
        return null;
    }
}

/// <summary>Kategoriye göre karşılaştırma stratejisini seçer; eşleşme yoksa genel/varsayılan stratejiye düşer.</summary>
public class CategoryComparisonStrategyRegistry : ICategoryComparisonStrategyRegistry
{
    private readonly Dictionary<HakedisCategory, ICategoryComparisonStrategy> _byCategory;
    private readonly ICategoryComparisonStrategy _default;

    public CategoryComparisonStrategyRegistry(IEnumerable<ICategoryComparisonStrategy> strategies)
    {
        var all = strategies.ToList();
        _default = all.First(s => s.Category is null);
        _byCategory = all.Where(s => s.Category.HasValue).ToDictionary(s => s.Category!.Value);
    }

    public ICategoryComparisonStrategy Get(HakedisCategory? category) =>
        category.HasValue && _byCategory.TryGetValue(category.Value, out var strategy) ? strategy : _default;
}
