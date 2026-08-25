using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Mağaza kesinleştirme motoru. AI yalnızca aday (kod/ad + güven skoru) üretir;
/// kesin karar burada, mağaza ana listesine karşı, sabit öncelik sırasıyla verilir:
/// 1) tam kod  2) normalize kod  3) tam ad  4) fuzzy ad (yüksek eşik)  5) belirsizse MANUAL_REVIEW.
/// </summary>
public class StoreMatchingService : IStoreMatchingService
{
    private const double FuzzyNameAcceptThreshold = 0.85;

    private readonly AppDbContext _db;

    public StoreMatchingService(AppDbContext db)
    {
        _db = db;
    }

    public string NormalizeCode(string? code) => TextNormalizationHelper.NormalizeCode(code);
    public string NormalizeName(string? name) => TextNormalizationHelper.NormalizeName(name);

    public async Task<StoreMatchResultDto> MatchAsync(string companyName, string region, string? codeRaw, string? nameRaw)
    {
        var stores = await _db.Stores
            .Where(s => s.CompanyName == companyName && s.Region == region && s.IsActive)
            .ToListAsync();

        if (stores.Count == 0)
            return new StoreMatchResultDto { Method = StoreMatchMethod.None, Confidence = 0 };

        // 1) Tam kod eşleşmesi (ham metin, kırpılmış)
        var codeTrimmed = codeRaw?.Trim();
        if (!string.IsNullOrEmpty(codeTrimmed))
        {
            var exact = stores.FirstOrDefault(s => string.Equals(s.Code, codeTrimmed, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return Result(exact.Id, $"{exact.Code} — {exact.Name}", StoreMatchMethod.ExactCode, 1.0m);

            // 2) Normalize edilmiş kod eşleşmesi (boşluk/tire farkları)
            var normCode = NormalizeCode(codeTrimmed);
            if (!string.IsNullOrEmpty(normCode))
            {
                var normMatches = stores.Where(s => s.NormalizedCode == normCode).ToList();
                if (normMatches.Count == 1)
                {
                    var m = normMatches[0];
                    return Result(m.Id, $"{m.Code} — {m.Name}", StoreMatchMethod.NormalizedCode, 0.98m);
                }
                if (normMatches.Count > 1)
                    return new StoreMatchResultDto { Method = StoreMatchMethod.ManualReview, Confidence = 0.5m };
            }
        }

        // 3) Tam ad eşleşmesi
        var nameTrimmed = nameRaw?.Trim();
        if (!string.IsNullOrEmpty(nameTrimmed))
        {
            var normName = NormalizeName(nameTrimmed);
            var exactName = stores.Where(s => s.NormalizedName == normName).ToList();
            if (exactName.Count == 1)
            {
                var m = exactName[0];
                return Result(m.Id, $"{m.Code} — {m.Name}", StoreMatchMethod.ExactName, 0.95m);
            }
            if (exactName.Count > 1)
                return new StoreMatchResultDto { Method = StoreMatchMethod.ManualReview, Confidence = 0.5m };

            // 4) Fuzzy ad eşleşmesi (yüksek eşik altında asla otomatik kabul edilmez)
            var scored = stores
                .Select(s => (Store: s, Score: TextNormalizationHelper.SimilarityRatio(normName, s.NormalizedName)))
                .OrderByDescending(x => x.Score)
                .ToList();
            var best = scored.FirstOrDefault();
            if (best.Store != null && best.Score >= FuzzyNameAcceptThreshold)
            {
                // İkinci en iyi adayla çok yakınsa (belirsiz) otomatik karar verme
                var second = scored.Skip(1).FirstOrDefault();
                if (second.Store == null || best.Score - second.Score >= 0.05)
                    return Result(best.Store.Id, $"{best.Store.Code} — {best.Store.Name}", StoreMatchMethod.FuzzyName, (decimal)best.Score);
            }
        }

        // 5) Belirsiz — otomatik karar verilmez
        return new StoreMatchResultDto { Method = StoreMatchMethod.ManualReview, Confidence = 0 };
    }

    private static StoreMatchResultDto Result(int storeId, string label, StoreMatchMethod method, decimal confidence) => new()
    {
        StoreId = storeId,
        StoreLabel = label,
        Method = method,
        Confidence = confidence,
    };
}
