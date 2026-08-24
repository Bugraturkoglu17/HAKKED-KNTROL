using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace HakedisOtomasyon.Infrastructure.FileStorage;

public class FileStorageService : IFileStorageService
{
    private readonly IAppPathService _appPath;
    private readonly IDisciplineProfileService _disciplineProfiles;
    private readonly CurrentDisciplineService _currentDiscipline;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(
        IAppPathService appPath,
        IDisciplineProfileService disciplineProfiles,
        CurrentDisciplineService currentDiscipline,
        ILogger<FileStorageService> logger)
    {
        _appPath = appPath;
        _disciplineProfiles = disciplineProfiles;
        _currentDiscipline = currentDiscipline;
        _logger = logger;
    }

    /// <summary>
    /// Yüklenen dosyayı aktif disiplinin Uploads alt klasörüne de kopyalar (Yangın → Yangın\Uploads,
    /// Klima → Klima\Uploads). Canonical (DB'de kayıtlı, AppPath.DataRootPath'e göreceli) yol
    /// DEĞİŞMEZ — bu sadece kullanıcının "... Klasörünü Aç" butonunda dosyayı görmesi içindir.
    /// </summary>
    private void MirrorToDisciplineFolder(string absoluteSourcePath, string fileName, bool isInvoice)
    {
        try
        {
            var discipline = _currentDiscipline.CurrentDiscipline;
            var targetDir = isInvoice
                ? _disciplineProfiles.GetInvoicesPath(discipline)
                : _disciplineProfiles.GetServiceFormsPath(discipline);
            Directory.CreateDirectory(targetDir);
            var targetPath = GetUniqueFilePath(targetDir, fileName);
            File.Copy(absoluteSourcePath, targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dosya Yangın klasörüne kopyalanamadı: {File}", fileName);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Stream tabanlı kaydetme — göreceli yol döner
    // ────────────────────────────────────────────────────────────────────────

    public async Task<string> SaveServiceFormAsync(Stream stream, string fileName, int year, int month)
    {
        var targetFolder = _appPath.GetServiceFormsFolder(year, month);
        var targetPath = GetUniqueFilePath(targetFolder, SanitizeFileName(fileName));
        await WriteStreamAsync(stream, targetPath);
        MirrorToDisciplineFolder(targetPath, Path.GetFileName(targetPath), isInvoice: false);
        return _appPath.ToRelativePath(targetPath);
    }

    public async Task<string> SaveInvoiceAsync(Stream stream, string fileName, int year, int month)
    {
        var targetFolder = _appPath.GetInvoicesFolder(year, month);
        var targetPath = GetUniqueFilePath(targetFolder, SanitizeFileName(fileName));
        await WriteStreamAsync(stream, targetPath);
        MirrorToDisciplineFolder(targetPath, Path.GetFileName(targetPath), isInvoice: true);
        return _appPath.ToRelativePath(targetPath);
    }

    public async Task<string> SaveExportAsync(Stream stream, string fileName, int year, int month)
    {
        var targetFolder = _appPath.GetExcelExportsFolder(year, month);
        var targetPath = GetUniqueFilePath(targetFolder, SanitizeFileName(fileName));
        await WriteStreamAsync(stream, targetPath);
        return _appPath.ToRelativePath(targetPath);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Dosya kopyalama — göreceli yol döner
    // ────────────────────────────────────────────────────────────────────────

    public Task<string> CopyServiceFormAsync(string sourcePath, int year, int month)
    {
        ValidateSourcePath(sourcePath);
        var targetFolder = _appPath.GetServiceFormsFolder(year, month);
        var targetPath = GetUniqueFilePath(targetFolder, SanitizeFileName(Path.GetFileName(sourcePath)));
        File.Copy(sourcePath, targetPath, overwrite: false);
        MirrorToDisciplineFolder(targetPath, Path.GetFileName(targetPath), isInvoice: false);
        return Task.FromResult(_appPath.ToRelativePath(targetPath));
    }

    public Task<string> CopyInvoiceAsync(string sourcePath, int year, int month)
    {
        ValidateSourcePath(sourcePath);
        var targetFolder = _appPath.GetInvoicesFolder(year, month);
        var targetPath = GetUniqueFilePath(targetFolder, SanitizeFileName(Path.GetFileName(sourcePath)));
        File.Copy(sourcePath, targetPath, overwrite: false);
        MirrorToDisciplineFolder(targetPath, Path.GetFileName(targetPath), isInvoice: true);
        return Task.FromResult(_appPath.ToRelativePath(targetPath));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Path yardımcıları
    // ────────────────────────────────────────────────────────────────────────

    public string GetAbsolutePath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return string.Empty;
        // Eski kayıtlar mutlak yol içerebilir — olduğu gibi kullan
        return Path.IsPathRooted(storedPath)
            ? storedPath
            : _appPath.ToAbsolutePath(storedPath);
    }

    public string GetFileUrl(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return string.Empty;
        var absPath = GetAbsolutePath(storedPath);
        // DataRootPath altındaki göreceli yolu hesapla
        var relPath = _appPath.ToRelativePath(absPath).Replace('\\', '/');
        return "https://localfiles.hakedis/" + relPath;
    }

    public bool FileExists(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return false;
        return File.Exists(GetAbsolutePath(storedPath));
    }

    public void DeleteFile(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return;
        var abs = GetAbsolutePath(storedPath);
        if (File.Exists(abs)) File.Delete(abs);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Private yardımcı metodlar
    // ────────────────────────────────────────────────────────────────────────

    private static void ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Kaynak dosya yolu bos olamaz.", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Kaynak dosya bulunamadi.", sourcePath);
    }

    private static string GetUniqueFilePath(string folder, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName);
        var counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{name}_{counter}{ext}");
            counter++;
        }
        return path;
    }

    private static async Task WriteStreamAsync(Stream stream, string targetPath)
    {
        stream.Position = 0;
        await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(fs);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(c => invalid.Contains(c) ? '_' : c));
    }
}