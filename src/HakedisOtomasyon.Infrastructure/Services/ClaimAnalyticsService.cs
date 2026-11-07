using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.Analytics;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

public class ClaimAnalyticsService : IClaimAnalyticsService
{
    private readonly AppDbContext _db;
    private readonly CurrentDisciplineService _currentDiscipline;

    public ClaimAnalyticsService(AppDbContext db, CurrentDisciplineService currentDiscipline)
    {
        _db = db;
        _currentDiscipline = currentDiscipline;
    }

    public async Task<Period?> GetLatestPeriodAsync()
    {
        var discipline = _currentDiscipline.CurrentDiscipline;
        var latest = await _db.ProgressClaims.AsNoTracking()
            .Where(c => c.Discipline == discipline)
            .OrderByDescending(c => c.Year).ThenByDescending(c => c.Month)
            .Select(c => new { c.Year, c.Month })
            .FirstOrDefaultAsync();
        return latest is null ? null : new Period(latest.Year, latest.Month);
    }

    public async Task<Period?> GetEarliestPeriodAsync()
    {
        var discipline = _currentDiscipline.CurrentDiscipline;
        var earliest = await _db.ProgressClaims.AsNoTracking()
            .Where(c => c.Discipline == discipline && c.Month >= 1 && c.Month <= 12)
            .OrderBy(c => c.Year).ThenBy(c => c.Month)
            .Select(c => new { c.Year, c.Month })
            .FirstOrDefaultAsync();
        return earliest is null ? null : new Period(earliest.Year, earliest.Month);
    }

    public async Task<ClaimAnalyticsResult> GetAsync(AnalyticsFilter filter)
    {
        var discipline = _currentDiscipline.CurrentDiscipline;
        // Önceki dönem karşılaştırması için aralık bir dönem geriden başlar
        var from = filter.Previous().From;
        var fromIdx = from.Year * 12 + from.Month - 1;
        var toIdx = filter.To.Year * 12 + filter.To.Month - 1;

        var claims = await _db.ProgressClaims.AsNoTracking()
            .Where(c => c.Discipline == discipline)
            .Where(c => c.Year * 12 + c.Month - 1 >= fromIdx && c.Year * 12 + c.Month - 1 <= toIdx)
            .Include(c => c.ServiceForms.Where(f => !f.IsArchived)).ThenInclude(f => f.Store)
            .Include(c => c.ServiceForms.Where(f => !f.IsArchived)).ThenInclude(f => f.ServiceItems)
            .AsSplitQuery()
            .ToListAsync();

        var headers = claims.Select(c => new ClaimHeaderRow(c.Id, new Period(c.Year, c.Month), c.CompanyName,
            c.Status == ProgressClaimStatus.DisaAktarildi)).ToList();

        var forms = claims.SelectMany(c => c.ServiceForms.Select(f => new ClaimFormRow
        {
            ClaimId = c.Id,
            Period = new Period(c.Year, c.Month),
            CompanyName = c.CompanyName,
            ClaimExported = c.Status == ProgressClaimStatus.DisaAktarildi,
            Category = CategoryLabel(c.Discipline, c.CoolingCategory, c.ClaimType),
            IsGlycolUsage = GlycolClaim.IsGlycol(c.Discipline, c.CoolingCategory),
            IsGasUsage = GlycolClaim.IsGas(c.Discipline, c.CoolingCategory),
            FormId = f.Id,
            FilePath = f.FilePath,
            IsCompleted = f.Status is ServiceFormStatus.Tamamlandi or ServiceFormStatus.DisaAktarildi,
            StoreId = f.StoreId,
            StoreCode = f.Store?.Code,
            StoreName = f.Store?.Name,
            City = f.Store?.City,
            Region = f.Store?.Region,
            FormDate = f.FormDate,
            UploadedAt = f.UploadedAt,
            Items = f.ServiceItems.Select(i => new ClaimItemRow
            {
                Description = i.Description,
                ItemCode = i.ItemCode,
                Unit = i.Unit,
                Quantity = i.Quantity,
                MaterialPrice = i.MaterialPrice,
                LaborPrice = i.LaborPrice,
                TotalPrice = i.TotalPrice,
                IsInvoiceBased = i.IsInvoiceBased,
                IsCurrencyBased = i.IsCurrencyBased,
                CurrencyCode = i.CurrencyCode,
                ExchangeRate = i.ExchangeRate,
            }).ToList(),
        })).ToList();

        return ClaimAnalyticsCalculator.Calculate(filter, headers, forms);
    }

    private static string CategoryLabel(MechanicalDiscipline discipline, CoolingClaimCategory? cooling, ProgressClaimType type)
    {
        if (discipline == MechanicalDiscipline.Cooling && cooling.HasValue) return cooling.Value.DisplayName();
        return type switch
        {
            ProgressClaimType.NewConstruction => "Yeni Yapım",
            ProgressClaimType.Renovation => "Tadilat",
            _ => "Bakım / Onarım",
        };
    }
}
