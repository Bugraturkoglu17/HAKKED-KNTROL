using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private readonly AppDbContext _db;
    private readonly CurrentDisciplineService _currentDiscipline;
    private readonly IDisciplineProfileService _disciplineProfiles;

    public DashboardService(
        AppDbContext db,
        CurrentDisciplineService currentDiscipline,
        IDisciplineProfileService disciplineProfiles)
    {
        _db = db;
        _currentDiscipline = currentDiscipline;
        _disciplineProfiles = disciplineProfiles;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(int year, int month, string? route = null)
    {
        var discipline = _currentDiscipline.CurrentDiscipline;

        var claims = await _db.ProgressClaims
            .Include(c => c.ServiceForms).ThenInclude(f => f.ServiceItems)
            .Where(c => c.Year == year && c.Month == month && c.Discipline == discipline)
            .ToListAsync();

        var allForms = claims.SelectMany(c => c.ServiceForms).ToList();
        var allItems = allForms.SelectMany(f => f.ServiceItems).ToList();
        var invoiceItems = allItems.Where(i => i.IsInvoiceBased).ToList();

        var recentExports = await _db.ExportLogs
            .Include(e => e.ProgressClaim)
            .Where(e => e.ProgressClaim.Discipline == discipline)
            .OrderByDescending(e => e.ExportedAt)
            .Take(5)
            .Select(e => new RecentExportDto
            {
                Id = e.Id,
                ClaimName = e.ProgressClaim.Name,
                FilePath = e.FilePath,
                ExportedAt = e.ExportedAt,
                ExportTypeLabel = e.ExportType == ExportType.Excel ? "Excel" : "PDF"
            }).ToListAsync();

        var summary = new DashboardSummaryDto
        {
            CurrentYear = year,
            CurrentMonth = month,
            TotalForms = allForms.Count,
            ProcessedForms = allForms.Count(f =>
                f.Status == ServiceFormStatus.Tamamlandi || f.Status == ServiceFormStatus.DisaAktarildi),
            IncompleteForms = allForms.Count(f =>
                f.Status == ServiceFormStatus.Bekliyor || f.Status == ServiceFormStatus.EksikBilgi),
            TotalClaimAmount = allItems.Sum(i => i.TotalPrice),
            InvoiceItemCount = invoiceItems.Count,
            RecentExports = recentExports,
            ActiveClaimsCount = claims.Count(c => c.Status != ProgressClaimStatus.DisaAktarildi)
        };

        WriteDashboardLog(discipline, claims.Count, summary.TotalClaimAmount);
        await WriteRuntimeLogAsync(discipline, route, claims.Count, summary.TotalClaimAmount);

        return summary;
    }

    private void WriteDashboardLog(MechanicalDiscipline discipline, int claimCount, decimal totalAmount)
    {
        try
        {
            var logDir = Path.Combine(_disciplineProfiles.GetCommonPath(), "Logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "dashboard-discipline-log.txt");
            var lines = new[]
            {
                $"=== Panel — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"Aktif Disiplin: {discipline}",
                $"Hakediş Sayısı: {claimCount}",
                $"Toplam Tutar: {totalAmount:N2} TL",
                $"Export Klasörü: {_disciplineProfiles.GetExportsPath(discipline)}",
                $"Fiyat Listesi Filtresi: PriceItems.Discipline == {discipline}",
                string.Empty
            };
            File.AppendAllLines(logFile, lines);
        }
        catch
        {
            // Log yazılamazsa sessizce atla — panel açılışını engellemez
        }
    }

    /// <summary>
    /// Aşama 6: her modül (Yangın/Klima/Asansör/Soğutma) panele her girişte
    /// route, kullanılan klasörler ve kayıt sayılarını loglar — izolasyon hatalarını
    /// (örn. yanlış disiplin verisi görünmesi) hızlıca tespit etmek içindir.
    /// </summary>
    private async Task WriteRuntimeLogAsync(MechanicalDiscipline discipline, string? route, int claimCount, decimal totalAmount)
    {
        try
        {
            var priceItemCount = await _db.PriceItems.CountAsync(p => p.Discipline == discipline);
            var storeCount = await _db.Stores.CountAsync(s => s.Discipline == discipline);

            var logDir = Path.Combine(_disciplineProfiles.GetCommonPath(), "Logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "discipline-runtime-log.txt");
            var lines = new[]
            {
                $"=== Modül Açılışı — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"Route: {route ?? "(bilinmiyor)"}",
                $"Aktif Disiplin: {discipline}",
                $"MasterData: {_disciplineProfiles.GetMasterDataPath(discipline)}",
                $"Uploads: {_disciplineProfiles.GetUploadsPath(discipline)}",
                $"Exports: {_disciplineProfiles.GetExportsPath(discipline)}",
                $"Panel Toplam Hakediş Sayısı: {claimCount}",
                $"Panel Toplam Tutar: {totalAmount:N2} TL",
                $"Fiyat Listesi Kayıt Sayısı: {priceItemCount}",
                $"Mağaza Kayıt Sayısı: {storeCount}",
                string.Empty
            };
            File.AppendAllLines(logFile, lines);
        }
        catch
        {
            // Log yazılamazsa sessizce atla — panel açılışını engellemez
        }
    }
}
