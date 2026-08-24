using System.Security.Cryptography;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HakedisOtomasyon.Infrastructure.FileStorage;

public class FileCleanupService : IFileCleanupService
{
    private readonly IAppPathService _appPath;
    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<FileCleanupService> _logger;

    public FileCleanupService(
        IAppPathService appPath,
        AppDbContext db,
        ISettingsService settings,
        ILogger<FileCleanupService> logger)
    {
        _appPath = appPath;
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    // ─── Dosya arşivleme ─────────────────────────────────────────────────

    public Task<string> ArchiveFileAsync(string relativeSourcePath, ArchiveCategory category, int year, int month)
    {
        if (string.IsNullOrWhiteSpace(relativeSourcePath))
            return Task.FromResult(string.Empty);

        var absSource = _appPath.ToAbsolutePath(relativeSourcePath);
        if (!File.Exists(absSource))
            return Task.FromResult(relativeSourcePath); // Dosya zaten yok; mevcut yolu döndür

        var archiveDir = category switch
        {
            ArchiveCategory.ServiceForm  => _appPath.GetArchiveServiceFormsFolder(year, month),
            ArchiveCategory.Invoice       => _appPath.GetArchiveInvoicesFolder(year, month),
            ArchiveCategory.ExcelExport   => _appPath.GetArchiveExcelExportsFolder(year, month),
            _                             => _appPath.GetArchiveFolder(year, month)
        };

        var fileName    = Path.GetFileNameWithoutExtension(absSource);
        var ext         = Path.GetExtension(absSource);
        var timestamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var archiveName = $"{fileName}_{timestamp}{ext}";
        var absTarget   = GetUniqueFilePath(archiveDir, archiveName);

        try
        {
            File.Move(absSource, absTarget);
        }
        catch (IOException)
        {
            throw new IOException(
                "Dosya başka bir program tarafından kullanıldığı için arşive taşınamadı. " +
                "Lütfen dosyayı kapatıp tekrar deneyin.");
        }

        return Task.FromResult(_appPath.ToRelativePath(absTarget));
    }

    // ─── Eski arşiv temizleme ─────────────────────────────────────────────

    public async Task<int> CleanupArchiveOlderThanAsync(int days)
    {
        if (days <= 0) return 0; // 0 = süresiz

        if (!Directory.Exists(_appPath.HakedislerPath))
            return 0;

        var cutoff = DateTime.Now.AddDays(-days);
        var deleted = 0;

        await Task.Run(() =>
        {
            foreach (var archiveDir in Directory.EnumerateDirectories(
                         _appPath.HakedislerPath, "Arşiv", SearchOption.AllDirectories))
            {
                foreach (var file in Directory.EnumerateFiles(archiveDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTime < cutoff)
                        {
                            info.Delete();
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Arşiv dosyası silinirken hata: {File}", file);
                    }
                }
            }
        });

        return deleted;
    }

    // ─── Eski Excel exportlarını arşivle ────────────────────────────────

    public async Task MoveOldExportsToArchiveAsync(int progressClaimId, int year, int month)
    {
        var activeExports = await _db.ExportLogs
            .Where(e => e.ProgressClaimId == progressClaimId
                        && e.ExportType == Domain.Enums.ExportType.Excel
                        && e.IsActive)
            .ToListAsync();

        foreach (var export in activeExports)
        {
            try
            {
                var newRelPath = await ArchiveFileAsync(export.FilePath, ArchiveCategory.ExcelExport, year, month);
                export.FilePath    = newRelPath;
                export.IsActive    = false;
                export.IsArchived  = true;
                export.ArchivedAt  = DateTime.Now;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Export arşivlenemedi: {FilePath}", export.FilePath);
            }
        }

        if (activeExports.Count > 0)
            await _db.SaveChangesAsync();
    }

    // ─── Servis formu arşivleme ───────────────────────────────────────────

    public async Task ArchiveRemovedServiceFormAsync(int serviceFormId)
    {
        var form = await _db.ServiceForms
            .Include(f => f.ProgressClaim)
            .FirstOrDefaultAsync(f => f.Id == serviceFormId);

        if (form is null) return;

        var year  = form.ProgressClaim.Year;
        var month = form.ProgressClaim.Month;

        if (!string.IsNullOrWhiteSpace(form.FilePath))
        {
            var newRelPath = await ArchiveFileAsync(form.FilePath, ArchiveCategory.ServiceForm, year, month);
            form.FilePath   = newRelPath;
        }

        form.IsArchived = true;
        form.ArchivedAt = DateTime.Now;
        await _db.SaveChangesAsync();
    }

    // ─── Fatura silme (hard delete) ───────────────────────────────────────

    public async Task ArchiveRemovedInvoiceAsync(int invoiceId)
    {
        var invoice = await _db.Invoices
            .Include(i => i.ServiceForm).ThenInclude(f => f.ProgressClaim)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice is null) return;

        var year  = invoice.ServiceForm?.ProgressClaim?.Year ?? DateTime.Now.Year;
        var month = invoice.ServiceForm?.ProgressClaim?.Month ?? DateTime.Now.Month;

        // Fiziksel dosyayı arşiv klasörüne taşı (başarısız olsa bile DB silme devam eder)
        if (!string.IsNullOrWhiteSpace(invoice.FilePath))
        {
            try
            {
                await ArchiveFileAsync(invoice.FilePath, ArchiveCategory.Invoice, year, month);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fatura dosyası arşivlenemedi, yalnızca DB kaydı siliniyor: {FilePath}", invoice.FilePath);
            }
        }

        // Hard delete — entity tamamen DB ve change tracker'dan kaldırılır
        _db.Invoices.Remove(invoice);
        await _db.SaveChangesAsync();
    }

    // ─── Başlangıç temizliği ──────────────────────────────────────────────

    public async Task RunStartupCleanupAsync()
    {
        try
        {
            var retentionStr = await _settings.GetAsync(SettingKeys.ArchiveRetentionDays, "30");
            if (!int.TryParse(retentionStr, out var days))
                days = 30;

            if (days > 0)
            {
                var deleted = await CleanupArchiveOlderThanAsync(days);
                if (deleted > 0)
                    _logger.LogInformation("Arşiv temizlendi: {Count} dosya silindi ({Days} günden eski).", deleted, days);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Arşiv başlangıç temizliği sırasında hata oluştu.");
        }
    }

    // ─── Yardımcılar ─────────────────────────────────────────────────────

    private static string GetUniqueFilePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        var i    = 1;
        do
        {
            path = Path.Combine(folder, $"{name}_{i++}{ext}");
        } while (File.Exists(path));

        return path;
    }

    // ─── SHA256 hash (FileStorageService için public yardımcı) ───────────

    public static async Task<string> ComputeFileHashAsync(string absolutePath)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(absolutePath);
        var hashBytes = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
