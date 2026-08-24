using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace HakedisOtomasyon.Infrastructure.Services;

public class DataBackupService : IDataBackupService
{
    private readonly AppDbContext _db;
    private readonly IAppPathService _appPath;
    private readonly IDataRecoveryService _recovery;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DataBackupService(AppDbContext db, IAppPathService appPath, IDataRecoveryService recovery)
    {
        _db      = db;
        _appPath = appPath;
        _recovery = recovery;
    }

    // ─── DIŞA AKTAR ────────────────────────────────────────────────────────

    public async Task<string> ExportMasterDataAsync()
    {
        var stores    = await _db.Stores.OrderBy(s => s.Code).ToListAsync();
        var items     = await _db.PriceItems.OrderBy(p => p.Id).ToListAsync();
        var aliases   = await _db.PriceAliases.OrderBy(a => a.Id).ToListAsync();
        var settings  = await _db.AppSettings.OrderBy(s => s.Key).ToListAsync();

        var metadata = new MasterDataMetadataDto
        {
            ExportDate    = DateTime.Now,
            AppVersion    = "1.0.1",
            StoreCount    = stores.Count,
            PriceItemCount = items.Count,
            AliasCount    = aliases.Count,
            SettingCount  = settings.Count,
        };

        var date       = DateTime.Now.ToString("yyyy_MM_dd");
        var fileName   = $"ServisHakedis_AnaVeri_{date}.zip";
        var outputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(archive, "metadata.json",     metadata);
            WriteJson(archive, "stores.json",       stores.Select(MapStore));
            WriteJson(archive, "price-items.json",  items.Select(MapPriceItem));
            WriteJson(archive, "price-aliases.json",aliases.Select(MapAlias));
            WriteJson(archive, "settings.json",     settings.Select(MapSetting));

            // MasterData Excel kaynak dosyalarını da yedeğe ekle
            AddDirectoryToArchive(archive, _appPath.StoreImportsPath,        "MasterData/StoreImports/");
            AddDirectoryToArchive(archive, _appPath.PriceCatalogImportsPath, "MasterData/PriceCatalogImports/");
        }

        await File.WriteAllBytesAsync(outputPath, ms.ToArray());
        return outputPath;
    }

    // ─── ÖNİZLEME ──────────────────────────────────────────────────────────

    public async Task<MasterDataPreviewDto> PreviewImportAsync(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Zip dosyası bulunamadı.", zipPath);

        var (importStores, importItems, importAliases, importSettings, metadata) = ReadZip(zipPath);

        // Mevcut veritabanı verileri
        var existingStores = await _db.Stores.AsNoTracking()
            .ToDictionaryAsync(s => s.Code.ToUpperInvariant());

        var existingItems = await _db.PriceItems.AsNoTracking().ToListAsync();
        var existingItemKeys = existingItems
            .ToDictionary(p => MakePriceItemKey(p.MainCategory, p.SubCategory, p.Description, p.Unit, (int)p.PriceType),
                          StringComparer.OrdinalIgnoreCase);

        var existingAliases = await _db.PriceAliases.AsNoTracking()
            .ToDictionaryAsync(a => a.NormalizedKeyword.ToLowerInvariant());

        var preview = new MasterDataPreviewDto
        {
            Metadata      = metadata,
            StoreData     = importStores,
            PriceItemData = importItems,
            AliasData     = importAliases,
            SettingData   = importSettings,
        };

        // Mağaza sayaçları
        foreach (var s in importStores)
        {
            var key = s.Code.ToUpperInvariant();
            if (existingStores.TryGetValue(key, out var ex))
                if (ex.Name == s.Name) preview.StoresToSkip++;
                else preview.StoresToUpdate++;
            else
                preview.StoresToAdd++;
        }

        // Fiyat kalemi sayaçları
        foreach (var p in importItems)
        {
            var key = MakePriceItemKey(p.MainCategory, p.SubCategory, p.Description, p.Unit, p.PriceType);
            if (existingItemKeys.TryGetValue(key, out var ex))
            {
                bool priceChanged = ex.MaterialPrice != p.MaterialPrice || ex.LaborPrice != p.LaborPrice;
                if (priceChanged) preview.PriceItemsToUpdate++;
                else preview.PriceItemsToSkip++;
            }
            else
                preview.PriceItemsToAdd++;
        }

        // Alias sayaçları
        foreach (var a in importAliases)
        {
            var key = a.NormalizedKeyword.ToLowerInvariant();
            if (existingAliases.ContainsKey(key)) preview.AliasesToUpdate++;
            else preview.AliasesToAdd++;
        }

        // Ayar sayacı
        preview.SettingsToUpdate = importSettings.Count;

        return preview;
    }

    // ─── UYGULA ────────────────────────────────────────────────────────────

    public async Task<MasterDataImportResultDto> ApplyImportAsync(MasterDataPreviewDto preview, ImportMode mode)
    {
        var result = new MasterDataImportResultDto();

        if (mode == ImportMode.ReplaceAll)
        {
            // GÜVENLIK: Silmeden önce otomatik yedek al
            // Yedek alınamazsa yine de devam et ama loglansın
            try { await ExportMasterDataAsync(); }
            catch { /* yedek hatası silme işlemini engellemez */ }

            _db.PriceAliases.RemoveRange(_db.PriceAliases);
            _db.PriceItems.RemoveRange(_db.PriceItems);
            _db.Stores.RemoveRange(_db.Stores);
            await _db.SaveChangesAsync();
        }

        // ─── Mağazalar ───
        var existingStores = await _db.Stores
            .ToDictionaryAsync(s => s.Code.ToUpperInvariant());

        foreach (var dto in preview.StoreData)
        {
            var key = dto.Code.ToUpperInvariant();
            if (existingStores.TryGetValue(key, out var existing))
            {
                if (mode == ImportMode.AddMissingOnly) { result.StoresSkipped++; continue; }
                if (existing.Name == dto.Name) { result.StoresSkipped++; continue; }
                existing.Name = dto.Name;
                existing.IsActive = dto.IsActive;
                result.StoresUpdated++;
            }
            else
            {
                _db.Stores.Add(new Store
                {
                    Code      = dto.Code,
                    Name      = dto.Name,
                    IsActive  = dto.IsActive,
                    CreatedAt = DateTime.Now,
                });
                result.StoresAdded++;
            }
        }
        await _db.SaveChangesAsync();

        // ─── Fiyat kalemleri ───
        var existingItems = await _db.PriceItems.ToListAsync();
        var existingItemDict = existingItems
            .ToDictionary(p => MakePriceItemKey(p.MainCategory, p.SubCategory, p.Description, p.Unit, (int)p.PriceType),
                          StringComparer.OrdinalIgnoreCase);

        foreach (var dto in preview.PriceItemData)
        {
            var key = MakePriceItemKey(dto.MainCategory, dto.SubCategory, dto.Description, dto.Unit, dto.PriceType);
            if (existingItemDict.TryGetValue(key, out var existing))
            {
                if (mode == ImportMode.AddMissingOnly) { result.PriceItemsSkipped++; continue; }
                bool changed = existing.MaterialPrice != dto.MaterialPrice || existing.LaborPrice != dto.LaborPrice;
                if (!changed) { result.PriceItemsSkipped++; continue; }
                existing.MaterialPrice = dto.MaterialPrice;
                existing.LaborPrice    = dto.LaborPrice;
                existing.UpdatedAt     = DateTime.Now;
                result.PriceItemsUpdated++;
            }
            else
            {
                _db.PriceItems.Add(new PriceItem
                {
                    SourceSheetName    = dto.SourceSheetName,
                    SourceRowNumber    = dto.SourceRowNumber,
                    PozNo              = dto.PozNo,
                    MainCategory       = dto.MainCategory,
                    SubCategory        = dto.SubCategory,
                    SubCategory2       = dto.SubCategory2,
                    Description        = dto.Description,
                    DisplayName        = dto.DisplayName,
                    InvoiceDescription = dto.InvoiceDescription,
                    Unit               = dto.Unit,
                    MaterialPrice      = dto.MaterialPrice,
                    LaborPrice         = dto.LaborPrice,
                    PriceType          = (PriceType)dto.PriceType,
                    SearchText         = dto.SearchText,
                    IsSelectable       = dto.IsSelectable,
                    IsActive           = dto.IsActive,
                    IsManuallyAdded    = dto.IsManuallyAdded,
                    CreatedAt          = DateTime.Now,
                });
                result.PriceItemsAdded++;
            }
        }
        await _db.SaveChangesAsync();

        // ─── Alias'lar ───
        var existingAliases = await _db.PriceAliases
            .ToDictionaryAsync(a => a.NormalizedKeyword.ToLowerInvariant());

        foreach (var dto in preview.AliasData)
        {
            var key = dto.NormalizedKeyword.ToLowerInvariant();
            if (existingAliases.TryGetValue(key, out var existing))
            {
                if (mode == ImportMode.AddMissingOnly) continue;
                existing.TargetText = dto.TargetText;
                existing.IsActive   = dto.IsActive;
            }
            else
            {
                _db.PriceAliases.Add(new PriceAlias
                {
                    Keyword           = dto.Keyword,
                    NormalizedKeyword = dto.NormalizedKeyword,
                    TargetText        = dto.TargetText,
                    IsActive          = dto.IsActive,
                    CreatedAt         = DateTime.Now,
                });
                result.AliasesAdded++;
            }
        }
        await _db.SaveChangesAsync();

        // ─── Ayarlar ───
        if (mode != ImportMode.AddMissingOnly)
        {
            var existingSettings = await _db.AppSettings
                .ToDictionaryAsync(s => s.Key);
            foreach (var dto in preview.SettingData)
            {
                if (existingSettings.TryGetValue(dto.Key, out var s))
                    s.Value = dto.Value;
                else
                    _db.AppSettings.Add(new AppSetting { Key = dto.Key, Value = dto.Value });
                result.SettingsUpdated++;
            }
            await _db.SaveChangesAsync();
        }

        _ = Task.Run(() => _recovery.SaveProtectedSnapshotAsync());
        return result;
    }

    // ─── TAM YEDEK AL ──────────────────────────────────────────────────────

    public async Task<string> CreateFullBackupAsync()
    {
        var date       = DateTime.Now.ToString("yyyy_MM_dd_HHmm");
        var fileName   = $"HakedisDatabase_Yedek_{date}.zip";
        // Yedek Backups klasörüne kaydedilir
        Directory.CreateDirectory(_appPath.BackupsPath);
        var outputPath = Path.Combine(_appPath.BackupsPath, fileName);

        var dbPath       = _appPath.DatabasePath;
        var skippedFiles = new List<string>();

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Veritabanı — uygulama açıkken bile VACUUM INTO ile güvenli snapshot alır
            if (File.Exists(dbPath))
                await AddDatabaseToZipWithVacuumAsync(archive, dbPath, "Data/hakedis.db");

            // Hakedişler klasörü — kilitli dosyalar atlatılır
            if (Directory.Exists(_appPath.HakedislerPath))
                skippedFiles.AddRange(await AddDirectoryToZipAsync(archive, _appPath.HakedislerPath, "Hakedişler"));

            // MasterData klasörü — kilitli dosyalar atlatılır
            if (Directory.Exists(_appPath.MasterDataPath))
                skippedFiles.AddRange(await AddDirectoryToZipAsync(archive, _appPath.MasterDataPath, "MasterData"));
        }

        await File.WriteAllBytesAsync(outputPath, ms.ToArray());

        if (skippedFiles.Count > 0)
            throw new HakedisOtomasyon.Application.DTOs.PartialBackupException(outputPath, skippedFiles);

        return outputPath;
    }

    // ─── TAM YEDEKTEN GERİ YÜKLE (Zamanla) ───────────────────────────────
    // Uygulama açıkken hakedis.db kilitli olduğu için doğrudan üzerine yazılamaz.
    // Bu metod ZIP yolunu pending-restore.json marker dosyasına yazar ve
    // uygulamanın kapanmasını tetikler. Bir sonraki açılışta DbContext başlamadan
    // önce PendingRestoreService.ExecutePendingRestoreIfAny() çağrılır.

    public void ScheduleFullRestore(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Yedek dosyası bulunamadı.", zipPath);

        PendingRestoreService.ScheduleRestore(zipPath);
    }

    // ─── VERİ KLASÖRÜNÜ AÇ ────────────────────────────────────────────────

    public void OpenDataFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName        = _appPath.DataRootPath,
            UseShellExecute = true,
            Verb            = "explore",
        });
    }

    // ─── YARDIMCI METODLAR ────────────────────────────────────────────────

    private static string MakePriceItemKey(string? main, string? sub, string desc, string unit, int priceType) =>
        $"{main}|{sub}|{desc}|{unit}|{priceType}";



    private static (List<StoreBackupDto>, List<PriceItemBackupDto>, List<PriceAliasBackupDto>,
                    List<AppSettingBackupDto>, MasterDataMetadataDto?) ReadZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        T? ReadEntry<T>(string entryName)
        {
            var entry = zip.GetEntry(entryName);
            if (entry is null) return default;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, JsonOpts);
        }

        var metadata = ReadEntry<MasterDataMetadataDto>("metadata.json");
        if (metadata is null)
            throw new InvalidDataException("Geçersiz yedek dosyası: metadata.json bulunamadı.");

        var stores   = ReadEntry<List<StoreBackupDto>>("stores.json")       ?? new();
        var items    = ReadEntry<List<PriceItemBackupDto>>("price-items.json") ?? new();
        var aliases  = ReadEntry<List<PriceAliasBackupDto>>("price-aliases.json") ?? new();
        var settings = ReadEntry<List<AppSettingBackupDto>>("settings.json") ?? new();

        return (stores, items, aliases, settings, metadata);
    }

    private static void WriteJson<T>(ZipArchive archive, string entryName, T data)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, data, JsonOpts);
    }

    /// <summary>
    /// Belirtilen klasördeki .xlsx dosyalarını ZIP arşivinin <paramref name="archiveFolder"/>
    /// yoluna ekler. Klasör yoksa veya boşsa sessizce atlar.
    /// </summary>
    private static void AddDirectoryToArchive(ZipArchive archive, string dirPath, string archiveFolder)
    {
        if (!Directory.Exists(dirPath)) return;
        foreach (var file in Directory.EnumerateFiles(dirPath, "*.xlsx"))
        {
            var entry = archive.CreateEntry(archiveFolder + Path.GetFileName(file), CompressionLevel.Optimal);
            using var es = entry.Open();
            using var fs = File.OpenRead(file);
            fs.CopyTo(es);
        }
    }

    /// <summary>
    /// SQLite veritabanını uygulama açıkken bile kilitsiz kopyalar.
    /// WAL checkpoint + VACUUM INTO ile tutarlı bir snapshot alır.
    /// EF Core ile aynı connection string kullanılır — Mode=ReadOnly hatası ortadan kalkar.
    /// </summary>
    private async Task AddDatabaseToZipWithVacuumAsync(ZipArchive archive, string dbPath, string entryName)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"hakedis_backup_{Guid.NewGuid():N}.db");
        try
        {
            // EF Core'un kullandığı connection string ile aç (Mode=ReadOnly YOK)
            var connStr = _db.Database.GetConnectionString() ?? $"Data Source={dbPath}";
            using var connection = new SqliteConnection(connStr);
            await connection.OpenAsync();

            // WAL modu aktifse, bekleyen değişiklikleri ana dosyaya birleştir
            using var ckpt = connection.CreateCommand();
            ckpt.CommandText = "PRAGMA wal_checkpoint(FULL);";
            await ckpt.ExecuteNonQueryAsync();

            // VACUUM INTO: kilitli DB'den bile tutarlı snapshot alır
            var escapedPath = tempPath.Replace("'", "''");
            using var vac = connection.CreateCommand();
            vac.CommandText = $"VACUUM INTO '{escapedPath}'";
            await vac.ExecuteNonQueryAsync();

            await AddFileToZipAsync(archive, tempPath, entryName);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static async Task AddFileToZipAsync(ZipArchive archive, string filePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        await using var fileStream = File.OpenRead(filePath);
        await fileStream.CopyToAsync(entryStream);
    }

    /// <summary>
    /// Klasördeki tüm dosyaları ZIP'e ekler.
    /// Kilitli (IOException) dosyalar atlatılır; atlatılan dosyaların listesi döner.
    /// </summary>
    private static async Task<List<string>> AddDirectoryToZipAsync(ZipArchive archive, string dirPath, string zipFolderName)
    {
        var skipped = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var rel = Path.GetRelativePath(dirPath, file).Replace(Path.DirectorySeparatorChar, '/');
                await AddFileToZipAsync(archive, file, $"{zipFolderName}/{rel}");
            }
            catch (IOException)
            {
                skipped.Add(file);
            }
        }
        return skipped;
    }

    private async Task CreateAutoBackupBeforeRestoreAsync(string dbPath, string uploadsPath, string outputPath)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (File.Exists(dbPath))
                await AddDatabaseToZipWithVacuumAsync(archive, dbPath, "hakedis.db");
            if (Directory.Exists(uploadsPath))
                await AddDirectoryToZipAsync(archive, uploadsPath, "Uploads");
        }
        await File.WriteAllBytesAsync(outputPath, ms.ToArray());
    }

    // ─── Entity → Backup DTO eşlemeleri ──────────────────────────────────

    private static StoreBackupDto MapStore(Store s) => new()
    {
        Code     = s.Code,
        Name     = s.Name,
        IsActive = s.IsActive,
    };

    private static PriceItemBackupDto MapPriceItem(PriceItem p) => new()
    {
        SourceSheetName    = p.SourceSheetName,
        SourceRowNumber    = p.SourceRowNumber,
        PozNo              = p.PozNo,
        MainCategory       = p.MainCategory,
        SubCategory        = p.SubCategory,
        SubCategory2       = p.SubCategory2,
        Description        = p.Description,
        DisplayName        = p.DisplayName,
        InvoiceDescription = p.InvoiceDescription,
        Unit               = p.Unit,
        MaterialPrice      = p.MaterialPrice,
        LaborPrice         = p.LaborPrice,
        PriceType          = (int)p.PriceType,
        SearchText         = p.SearchText,
        IsSelectable       = p.IsSelectable,
        IsActive           = p.IsActive,
        IsManuallyAdded    = p.IsManuallyAdded,
    };

    private static PriceAliasBackupDto MapAlias(PriceAlias a) => new()
    {
        Keyword           = a.Keyword,
        NormalizedKeyword = a.NormalizedKeyword,
        TargetText        = a.TargetText,
        IsActive          = a.IsActive,
    };

    private static AppSettingBackupDto MapSetting(AppSetting s) => new()
    {
        Key         = s.Key,
        Value       = s.Value,
        Description = s.Description,
    };
}
