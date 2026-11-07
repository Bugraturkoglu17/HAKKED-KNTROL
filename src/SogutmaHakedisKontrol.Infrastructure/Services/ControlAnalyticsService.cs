using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Application.Analytics;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

public class ControlAnalyticsService : IControlAnalyticsService
{
    private readonly AppDbContext _db;

    public ControlAnalyticsService(AppDbContext db) => _db = db;

    public async Task<(int Year, int Month)?> GetLatestPeriodAsync()
    {
        var latest = await _db.ProgressPaymentChecks.AsNoTracking()
            .Where(c => c.Month >= 1 && c.Month <= 12 && c.Year > 0)
            .OrderByDescending(c => c.Year).ThenByDescending(c => c.Month)
            .Select(c => new { c.Year, c.Month })
            .FirstOrDefaultAsync();
        return latest is null ? null : (latest.Year, latest.Month);
    }

    public async Task<ControlAnalyticsResult> GetAsync(ControlAnalyticsFilter filter)
    {
        // Firma/bölge seçenekleri tüm kayıtlardan gelsin diye dönem dışı başlıklar da okunur; kalemler yalnızca aralık için.
        var checks = await _db.ProgressPaymentChecks.AsNoTracking()
            .Where(c => c.Month >= 1 && c.Month <= 12 && c.Year > 0)
            .Select(c => new
            {
                c.Id, c.Year, c.Month, c.CompanyName, c.Region, c.ClaimTypeName, c.Category, c.PeriodLabel,
                c.Status, c.Stage, c.CompanyTotal, c.CalculatedTotal, c.Difference, c.CreatedAt,
            })
            .ToListAsync();

        var inRangeIds = checks.Where(c => c.Year * 12 + c.Month - 1 >= filter.FromIndex && c.Year * 12 + c.Month - 1 <= filter.ToIndex)
            .Select(c => c.Id).ToList();

        var items = await _db.ProgressPaymentCheckItems.AsNoTracking()
            .Where(i => inRangeIds.Contains(i.ProgressPaymentCheckId))
            .Select(i => new
            {
                i.ProgressPaymentCheckId, i.MatchedMaterialName, i.OriginalMaterialName, i.StoreCity, i.ControlStatus,
                i.CompanyLineTotal, i.Difference, i.DifferencePercent, i.IsExcluded,
            })
            .ToListAsync();

        var ai = await _db.AiComparisonResults.AsNoTracking()
            .Where(r => inRangeIds.Contains(r.Job.ProgressPaymentCheckId))
            .Select(r => new { r.Job.ProgressPaymentCheckId, r.Status, r.ItemType, r.UserOverridden })
            .ToListAsync();

        var itemsByCheck = items.ToLookup(i => i.ProgressPaymentCheckId);
        var aiByCheck = ai.ToLookup(a => a.ProgressPaymentCheckId);

        var rows = checks.Select(c => new ControlCheckRow
        {
            Id = c.Id,
            Year = c.Year,
            Month = c.Month,
            CompanyName = c.CompanyName,
            Region = c.Region,
            CategoryName = c.Category?.DisplayName() ?? c.ClaimTypeName,
            PeriodLabel = c.PeriodLabel,
            IsCompleted = c.Status == ProgressPaymentCheckStatus.Tamamlandi || c.Stage >= HakedisControlStage.ReadyForExport,
            CompanyTotal = c.CompanyTotal,
            CalculatedTotal = c.CalculatedTotal,
            Difference = c.Difference,
            CreatedAt = c.CreatedAt,
            Items = itemsByCheck[c.Id].Select(i => new ControlItemRow
            {
                MaterialName = string.IsNullOrWhiteSpace(i.MatchedMaterialName) ? i.OriginalMaterialName : i.MatchedMaterialName!,
                StoreCity = i.StoreCity,
                Status = i.ControlStatus,
                CompanyLineTotal = i.CompanyLineTotal,
                Difference = i.Difference,
                DifferencePercent = i.DifferencePercent,
                IsExcluded = i.IsExcluded,
            }).ToList(),
            AiResults = aiByCheck[c.Id].Select(a => new ControlAiRow
            {
                Status = a.Status,
                ItemType = a.ItemType,
                UserOverridden = a.UserOverridden,
            }).ToList(),
        }).ToList();

        return ControlAnalyticsCalculator.Calculate(filter, rows);
    }
}
