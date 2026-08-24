using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Mağaza ve Fiyat Kalemi verilerini AppData altındaki korunan konuma yedekler;
/// yeni boş veritabanı oluştuğunda eski verileri geri yükler.
/// </summary>
public class DataRecoveryService : IDataRecoveryService
{
    private readonly AppDbContext _db;
    private readonly IAppPathService _appPath;

    // Korunan yedek dizini — DataRootPath'ten bağımsız, AppData altında
    private static readonly string ProtectedDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServisHakedis", "ProtectedMasterData");

    private static readonly string StoresFile      = Path.Combine(ProtectedDir, "stores-latest.json");
    private static readonly string PriceItemsFile  = Path.Combine(ProtectedDir, "price-items-latest.json");
    private static readonly string AliasesFile     = Path.Combine(ProtectedDir, "price-aliases-latest.json");
    private static readonly string MetaFile        = Path.Combine(ProtectedDir, "meta.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DataRecoveryService(AppDbContext db, IAppPathService appPath)
    {
        _db      = db;
        _appPath = appPath;
    }

    // ─── HasProtectedSnapshot ───────────────────────────────────────────────

    public bool HasProtectedSnapshot =>
        File.Exists(StoresFile) || File.Exists(PriceItemsFile);

    // ─── TablesAreEmptyAsync ────────────────────────────────────────────────

    public async Task<bool> TablesAreEmptyAsync()
    {
        var storeCount     = await _db.Stores.CountAsync();
        var priceItemCount = await _db.PriceItems.CountAsync();
        return storeCount == 0 && priceItemCount == 0;
    }

    // ─── SaveProtectedSnapshotAsync ─────────────────────────────────────────

    public async Task SaveProtectedSnapshotAsync()
    {
        try
        {
            Directory.CreateDirectory(ProtectedDir);

            var stores  = await _db.Stores.AsNoTracking().OrderBy(s => s.Code).ToListAsync();
            var items   = await _db.PriceItems.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
            var aliases = await _db.PriceAliases.AsNoTracking().OrderBy(a => a.Id).ToListAsync();

            if (stores.Count > 0)
                await WriteJsonAsync(StoresFile, stores.Select(MapStore));

            if (items.Count > 0)
                await WriteJsonAsync(PriceItemsFile, items.Select(MapPriceItem));

            if (aliases.Count > 0)
                await WriteJsonAsync(AliasesFile, aliases.Select(MapAlias));

            var meta = new { SavedAt = DateTime.Now, StoreCount = stores.Count, PriceItemCount = items.Count, AliasCount = aliases.Count };
            await WriteJsonAsync(MetaFile, meta);
        }
        catch { /* Yedekleme hatası uygulamayı durdurmasın */ }
    }

    // ─── ScanForRecoverySourcesAsync ───────────────────────────────────────

    public async Task<DataRecoveryScanResult> ScanForRecoverySourcesAsync()
    {
        var result = new DataRecoveryScanResult();

        // 1. Korunan yedek kontrolü
        if (HasProtectedSnapshot)
        {
            result.HasProtectedData = true;
            result.ProtectedStoreCount      = await CountJsonItemsAsync<StoreSnapshot>(StoresFile);
            result.ProtectedPriceItemCount  = await CountJsonItemsAsync<PriceItemSnapshot>(PriceItemsFile);
            result.ProtectedAliasCount      = await CountJsonItemsAsync<AliasSnapshot>(AliasesFile);
            result.ProtectedDataDate        = GetFileDate(MetaFile) ?? GetFileDate(StoresFile) ?? GetFileDate(PriceItemsFile);
        }

        // 2. Eski veritabanı dosyalarını ara
        var candidatePaths = BuildCandidatePaths();
        var currentDb = _appPath.DatabasePath;

        foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            if (string.Equals(path, currentDb, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var (stores, items) = await ReadCountsFromSqliteAsync(path);
                if (stores > 0 || items > 0)
                {
                    result.FoundDatabases.Add(new OldDatabaseInfo
                    {
                        Path          = path,
                        Label         = BuildLabel(path),
                        StoreCount    = stores,
                        PriceItemCount = items,
                        LastModified  = File.GetLastWriteTime(path),
                    });
                }
            }
            catch { /* Bozuk veya erişilemeyen DB'yi atla */ }
        }

        // En yeniden eskiye sırala
        result.FoundDatabases = result.FoundDatabases
            .OrderByDescending(d => d.LastModified)
            .ToList();

        return result;
    }

    // ─── RestoreFromProtectedDataAsync ─────────────────────────────────────

    public async Task<DataRecoveryResult> RestoreFromProtectedDataAsync(
        bool includeStores, bool includePriceItems, bool includeAliases)
    {
        var result = new DataRecoveryResult();

        if (includeStores && File.Exists(StoresFile))
        {
            var stores = await ReadJsonAsync<List<StoreSnapshot>>(StoresFile);
            if (stores is not null)
                await MergeStoresAsync(stores, result);
        }

        if (includePriceItems && File.Exists(PriceItemsFile))
        {
            var items = await ReadJsonAsync<List<PriceItemSnapshot>>(PriceItemsFile);
            if (items is not null)
                await MergePriceItemsAsync(items, result);
        }

        if (includeAliases && File.Exists(AliasesFile))
        {
            var aliases = await ReadJsonAsync<List<AliasSnapshot>>(AliasesFile);
            if (aliases is not null)
                await MergeAliasesAsync(aliases, result);
        }

        return result;
    }

    // ─── RestoreFromDatabaseAsync ───────────────────────────────────────────

    public async Task<DataRecoveryResult> RestoreFromDatabaseAsync(
        string dbPath, bool includeStores, bool includePriceItems, bool includeAliases)
    {
        var result = new DataRecoveryResult();

        if (!File.Exists(dbPath))
        {
            result.Errors.Add($"Dosya bulunamadı: {dbPath}");
            return result;
        }

        var connStr = $"Data Source={dbPath};Mode=ReadOnly";
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        if (includeStores)
        {
            var stores = await ReadStoresFromSqliteAsync(conn);
            await MergeStoresAsync(stores, result);
        }

        if (includePriceItems)
        {
            var items = await ReadPriceItemsFromSqliteAsync(conn);
            await MergePriceItemsAsync(items, result);
        }

        if (includeAliases)
        {
            var aliases = await ReadAliasesFromSqliteAsync(conn);
            await MergeAliasesAsync(aliases, result);
        }

        return result;
    }

    // ─── MERGE: Stores ──────────────────────────────────────────────────────

    private async Task MergeStoresAsync(IEnumerable<StoreSnapshot> snapshots, DataRecoveryResult result)
    {
        var existing = await _db.Stores.ToDictionaryAsync(s => s.Code.ToUpperInvariant());

        foreach (var s in snapshots)
        {
            if (string.IsNullOrWhiteSpace(s.Code)) continue;
            var key = s.Code.ToUpperInvariant();

            if (existing.TryGetValue(key, out var ex))
            {
                if (ex.Name != s.Name || ex.IsActive != s.IsActive)
                {
                    ex.Name     = s.Name ?? ex.Name;
                    ex.IsActive = s.IsActive;
                    result.StoresUpdated++;
                }
            }
            else
            {
                _db.Stores.Add(new Store
                {
                    Code      = s.Code,
                    Name      = s.Name ?? s.Code,
                    IsActive  = s.IsActive,
                    CreatedAt = DateTime.Now,
                });
                result.StoresAdded++;
            }
        }

        try { await _db.SaveChangesAsync(); }
        catch (Exception ex) { result.Errors.Add($"Mağaza kayıt hatası: {ex.Message}"); }
    }

    // ─── MERGE: PriceItems ──────────────────────────────────────────────────

    private async Task MergePriceItemsAsync(IEnumerable<PriceItemSnapshot> snapshots, DataRecoveryResult result)
    {
        var existingItems = await _db.PriceItems.ToListAsync();
        var existingDict  = existingItems.ToDictionary(
            p => MakeKey(p.MainCategory, p.SubCategory, p.Description, p.Unit, (int)p.PriceType),
            StringComparer.OrdinalIgnoreCase);

        var batch = new List<PriceItem>();
        foreach (var s in snapshots)
        {
            if (string.IsNullOrWhiteSpace(s.Description)) continue;
            var key = MakeKey(s.MainCategory, s.SubCategory, s.Description, s.Unit, s.PriceType);

            if (existingDict.TryGetValue(key, out var ex))
            {
                bool changed = ex.MaterialPrice != s.MaterialPrice || ex.LaborPrice != s.LaborPrice;
                if (changed)
                {
                    ex.MaterialPrice = s.MaterialPrice;
                    ex.LaborPrice    = s.LaborPrice;
                    ex.UpdatedAt     = DateTime.Now;
                    result.PriceItemsUpdated++;
                }
            }
            else
            {
                batch.Add(new PriceItem
                {
                    SourceSheetName    = s.SourceSheetName,
                    SourceRowNumber    = s.SourceRowNumber,
                    PozNo              = s.PozNo,
                    MainCategory       = s.MainCategory,
                    SubCategory        = s.SubCategory,
                    SubCategory2       = s.SubCategory2,
                    Description        = s.Description,
                    DisplayName        = s.DisplayName ?? s.Description,
                    InvoiceDescription = s.InvoiceDescription,
                    Unit               = s.Unit ?? "",
                    MaterialPrice      = s.MaterialPrice,
                    LaborPrice         = s.LaborPrice,
                    PriceType          = (PriceType)s.PriceType,
                    SearchText         = s.SearchText,
                    IsSelectable       = s.IsSelectable,
                    IsActive           = s.IsActive,
                    IsManuallyAdded    = s.IsManuallyAdded,
                    HasMissingUnit     = s.HasMissingUnit,
                    IsCurrencyBased    = s.IsCurrencyBased,
                    CurrencyCode       = s.CurrencyCode,
                    ListPriceUsd       = s.ListPriceUsd,
                    DiscountRate       = s.DiscountRate,
                    DiscountedUsdPrice = s.DiscountedUsdPrice,
                    ExchangeRateRequired = s.ExchangeRateRequired,
                    CreatedAt          = DateTime.Now,
                });
                result.PriceItemsAdded++;

                // Toplu kayıt (her 500'de bir)
                if (batch.Count >= 500)
                {
                    _db.PriceItems.AddRange(batch);
                    try { await _db.SaveChangesAsync(); } catch (Exception saveEx) { result.Errors.Add(saveEx.Message); }
                    batch.Clear();
                }
            }
        }

        if (batch.Count > 0)
            _db.PriceItems.AddRange(batch);

        try { await _db.SaveChangesAsync(); }
        catch (Exception ex) { result.Errors.Add($"Fiyat kalemi kayıt hatası: {ex.Message}"); }
    }

    // ─── MERGE: Aliases ─────────────────────────────────────────────────────

    private async Task MergeAliasesAsync(IEnumerable<AliasSnapshot> snapshots, DataRecoveryResult result)
    {
        var existing = await _db.PriceAliases
            .ToDictionaryAsync(a => a.NormalizedKeyword.ToLowerInvariant());

        foreach (var s in snapshots)
        {
            if (string.IsNullOrWhiteSpace(s.NormalizedKeyword)) continue;
            var key = s.NormalizedKeyword.ToLowerInvariant();

            if (existing.TryGetValue(key, out var ex))
            {
                ex.TargetText = s.TargetText ?? ex.TargetText;
                ex.IsActive   = s.IsActive;
                result.AliasesUpdated++;
            }
            else
            {
                _db.PriceAliases.Add(new PriceAlias
                {
                    Keyword           = s.Keyword ?? s.NormalizedKeyword,
                    NormalizedKeyword = s.NormalizedKeyword,
                    TargetText        = s.TargetText ?? "",
                    IsActive          = s.IsActive,
                    CreatedAt         = DateTime.Now,
                });
                result.AliasesAdded++;
            }
        }

        try { await _db.SaveChangesAsync(); }
        catch { /* Alias hatası kritik değil */ }
    }

    // ─── SQLite doğrudan okuma (dış DB) ─────────────────────────────────────

    private static async Task<(int stores, int items)> ReadCountsFromSqliteAsync(string dbPath)
    {
        var connStr = $"Data Source={dbPath};Mode=ReadOnly";
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        int stores = await ScalarIntAsync(conn, "SELECT COUNT(*) FROM Stores");
        int items  = await ScalarIntAsync(conn, "SELECT COUNT(*) FROM PriceItems");
        return (stores, items);
    }

    private static async Task<List<StoreSnapshot>> ReadStoresFromSqliteAsync(SqliteConnection conn)
    {
        var list = new List<StoreSnapshot>();
        // Tablo varlığını kontrol et
        if (!await TableExistsAsync(conn, "Stores")) return list;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Code, Name, IsActive FROM Stores";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new StoreSnapshot
            {
                Code     = reader.GetString(0),
                Name     = reader.GetString(1),
                IsActive = reader.GetInt32(2) != 0,
            });
        }
        return list;
    }

    private static async Task<List<PriceItemSnapshot>> ReadPriceItemsFromSqliteAsync(SqliteConnection conn)
    {
        var list = new List<PriceItemSnapshot>();
        if (!await TableExistsAsync(conn, "PriceItems")) return list;

        // Mevcut sütun listesini al (eski şema uyumu için)
        var cols = await GetColumnsAsync(conn, "PriceItems");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildPriceItemSelect(cols);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadPriceItemRow(reader, cols));
        }
        return list;
    }

    private static async Task<List<AliasSnapshot>> ReadAliasesFromSqliteAsync(SqliteConnection conn)
    {
        var list = new List<AliasSnapshot>();
        if (!await TableExistsAsync(conn, "PriceAliases")) return list;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Keyword, NormalizedKeyword, TargetText, IsActive FROM PriceAliases";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new AliasSnapshot
            {
                Keyword           = SafeString(reader, 0),
                NormalizedKeyword = reader.GetString(1),
                TargetText        = SafeString(reader, 2) ?? "",
                IsActive          = reader.GetInt32(3) != 0,
            });
        }
        return list;
    }

    private static string BuildPriceItemSelect(HashSet<string> cols)
    {
        // Her zaman var olan temel sütunlar
        var sb = new System.Text.StringBuilder(
            "SELECT Description, Unit, MaterialPrice, LaborPrice");

        // Opsiyonel sütunlar
        foreach (var opt in new[] { "SourceSheetName", "SourceRowNumber", "PozNo",
            "MainCategory", "SubCategory", "SubCategory2", "DisplayName", "InvoiceDescription",
            "PriceType", "SearchText", "IsSelectable", "IsActive", "IsManuallyAdded",
            "HasMissingUnit", "IsCurrencyBased", "CurrencyCode",
            "ListPriceUsd", "DiscountRate", "DiscountedUsdPrice", "ExchangeRateRequired" })
        {
            if (cols.Contains(opt))
                sb.Append($", {opt}");
        }

        sb.Append(" FROM PriceItems");
        return sb.ToString();
    }

    private static PriceItemSnapshot ReadPriceItemRow(SqliteDataReader reader, HashSet<string> cols)
    {
        // Sütun adı → index eşlemesi oluştur
        var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
            nameToIdx[reader.GetName(i)] = i;

        return new PriceItemSnapshot
        {
            Description        = GetStr(reader, nameToIdx, "Description") ?? "",
            Unit               = GetStr(reader, nameToIdx, "Unit") ?? "",
            MaterialPrice      = GetDecimal(reader, nameToIdx, "MaterialPrice"),
            LaborPrice         = GetDecimal(reader, nameToIdx, "LaborPrice"),
            SourceSheetName    = GetStr(reader, nameToIdx, "SourceSheetName"),
            SourceRowNumber    = GetNullableInt(reader, nameToIdx, "SourceRowNumber"),
            PozNo              = GetStr(reader, nameToIdx, "PozNo"),
            MainCategory       = GetStr(reader, nameToIdx, "MainCategory"),
            SubCategory        = GetStr(reader, nameToIdx, "SubCategory"),
            SubCategory2       = GetStr(reader, nameToIdx, "SubCategory2"),
            DisplayName        = GetStr(reader, nameToIdx, "DisplayName"),
            InvoiceDescription = GetStr(reader, nameToIdx, "InvoiceDescription"),
            PriceType          = GetInt(reader, nameToIdx, "PriceType"),
            SearchText         = GetStr(reader, nameToIdx, "SearchText"),
            IsSelectable       = GetBool(reader, nameToIdx, "IsSelectable", true),
            IsActive           = GetBool(reader, nameToIdx, "IsActive", true),
            IsManuallyAdded    = GetBool(reader, nameToIdx, "IsManuallyAdded", false),
            HasMissingUnit     = GetBool(reader, nameToIdx, "HasMissingUnit", false),
            IsCurrencyBased    = GetBool(reader, nameToIdx, "IsCurrencyBased", false),
            CurrencyCode       = GetStr(reader, nameToIdx, "CurrencyCode"),
            ListPriceUsd       = GetNullableDecimal(reader, nameToIdx, "ListPriceUsd"),
            DiscountRate       = GetNullableDecimal(reader, nameToIdx, "DiscountRate"),
            DiscountedUsdPrice = GetNullableDecimal(reader, nameToIdx, "DiscountedUsdPrice"),
            ExchangeRateRequired = GetBool(reader, nameToIdx, "ExchangeRateRequired", false),
        };
    }

    // ─── Yardımcı: SQLite okuma ──────────────────────────────────────────────

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection conn, string tableName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            set.Add(reader.GetString(1));
        return set;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        catch { return 0; }
    }

    private static string? SafeString(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetString(i);

    private static string? GetStr(SqliteDataReader r, Dictionary<string, int> map, string col) =>
        map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetString(i) : null;

    private static int GetInt(SqliteDataReader r, Dictionary<string, int> map, string col, int def = 0) =>
        map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt32(i) : def;

    private static int? GetNullableInt(SqliteDataReader r, Dictionary<string, int> map, string col) =>
        map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt32(i) : null;

    private static decimal GetDecimal(SqliteDataReader r, Dictionary<string, int> map, string col) =>
        map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetDecimal(i) : 0m;

    private static bool GetBool(SqliteDataReader r, Dictionary<string, int> map, string col, bool def) =>
        map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetInt32(i) != 0 : def;

    private static decimal? GetNullableDecimal(SqliteDataReader r, Dictionary<string, int> map, string col) =>
        map.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetDecimal(i) : null;

    // ─── Aday yollar ────────────────────────────────────────────────────────

    private List<string> BuildCandidatePaths()
    {
        var desktop  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var appLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths    = new List<string>();

        // 1. Masaüstü \ HAKEDİŞ DATABASE
        var desktopDb = Path.Combine(desktop, "HAKEDİŞ DATABASE", "Data", "hakedis.db");
        paths.Add(desktopDb);

        // 2. Eski DataRootPath (varsa farklıysa)
        var currentDataRoot = _appPath.DataRootPath;
        var oldDbDir = Directory.GetParent(currentDataRoot)?.FullName;
        if (oldDbDir is not null)
        {
            foreach (var dir in Directory.EnumerateDirectories(oldDbDir))
            {
                var candidate = Path.Combine(dir, "Data", "hakedis.db");
                paths.Add(candidate);
            }
        }

        // 3. AppData\Local\ServisHakedis
        paths.Add(Path.Combine(appLocal, "ServisHakedis", "Data", "hakedis.db"));
        paths.Add(Path.Combine(appLocal, "ServisHakedis", "hakedis.db"));
        paths.Add(Path.Combine(appLocal, "HakedisOtomasyon", "hakedis.db"));

        // 4. Geçerli DataRoot kardeş klasörlerdeki DB'ler
        var dataRootParent = Directory.GetParent(_appPath.DataRootPath)?.FullName;
        if (dataRootParent is not null)
        {
            foreach (var dir in SafeEnumDirectories(dataRootParent))
            {
                paths.Add(Path.Combine(dir, "Data", "hakedis.db"));
                paths.Add(Path.Combine(dir, "hakedis.db"));
            }
        }

        // 5. Yedek ZIP içinden çıkarılmış db dosyaları (Backups klasörü)
        if (Directory.Exists(_appPath.BackupsPath))
        {
            foreach (var zipFile in Directory.EnumerateFiles(_appPath.BackupsPath, "*.zip"))
            {
                // ZIP içindeki db'yi geçici klasöre çıkar ve ekle (sonraki versiyona bırakılabilir)
                // Şimdilik atlıyoruz — kullanıcı "ZIP'ten kurtar" butonunu kullanabilir
            }
        }

        // 6. ServisHakedis exe dizini
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        paths.Add(Path.Combine(exeDir, "hakedis.db"));
        paths.Add(Path.Combine(exeDir, "Data", "hakedis.db"));

        return paths;
    }

    private static string BuildLabel(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath) ?? "";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var appLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (dbPath.StartsWith(desktop, StringComparison.OrdinalIgnoreCase))
            return "Masaüstü › " + Path.GetRelativePath(desktop, dbPath).Replace(Path.DirectorySeparatorChar, '/');
        if (dbPath.StartsWith(appLocal, StringComparison.OrdinalIgnoreCase))
            return "AppData › " + Path.GetRelativePath(appLocal, dbPath).Replace(Path.DirectorySeparatorChar, '/');
        return dbPath;
    }

    private static IEnumerable<string> SafeEnumDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return []; }
    }

    // ─── JSON yardımcıları ──────────────────────────────────────────────────

    private static async Task WriteJsonAsync<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(data, JsonOpts);
        await File.WriteAllTextAsync(path, json, System.Text.Encoding.UTF8);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    private static async Task<int> CountJsonItemsAsync<T>(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            var list = await ReadJsonAsync<List<T>>(path);
            return list?.Count ?? 0;
        }
        catch { return 0; }
    }

    private static DateTime? GetFileDate(string path)
    {
        if (!File.Exists(path)) return null;
        return File.GetLastWriteTime(path);
    }

    // ─── Anahtar oluşturma ───────────────────────────────────────────────────

    private static string MakeKey(string? main, string? sub, string desc, string? unit, int priceType) =>
        $"{main}|{sub}|{desc}|{unit}|{priceType}";

    // ─── Entity → Snapshot eşlemeleri ───────────────────────────────────────

    private static StoreSnapshot MapStore(Store s) => new()
    {
        Code     = s.Code,
        Name     = s.Name,
        IsActive = s.IsActive,
    };

    private static PriceItemSnapshot MapPriceItem(PriceItem p) => new()
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
        HasMissingUnit     = p.HasMissingUnit,
        IsCurrencyBased    = p.IsCurrencyBased,
        CurrencyCode       = p.CurrencyCode,
        ListPriceUsd       = p.ListPriceUsd,
        DiscountRate       = p.DiscountRate,
        DiscountedUsdPrice = p.DiscountedUsdPrice,
        ExchangeRateRequired = p.ExchangeRateRequired,
    };

    private static AliasSnapshot MapAlias(PriceAlias a) => new()
    {
        Keyword           = a.Keyword,
        NormalizedKeyword = a.NormalizedKeyword,
        TargetText        = a.TargetText,
        IsActive          = a.IsActive,
    };

    // ─── İç snapshot record'ları ─────────────────────────────────────────────

    private record StoreSnapshot
    {
        public string  Code     { get; init; } = "";
        public string? Name     { get; init; }
        public bool    IsActive { get; init; } = true;
    }

    private record PriceItemSnapshot
    {
        public string?  SourceSheetName    { get; init; }
        public int?     SourceRowNumber    { get; init; }
        public string?  PozNo              { get; init; }
        public string?  MainCategory       { get; init; }
        public string?  SubCategory        { get; init; }
        public string?  SubCategory2       { get; init; }
        public string   Description        { get; init; } = "";
        public string?  DisplayName        { get; init; }
        public string?  InvoiceDescription { get; init; }
        public string?  Unit               { get; init; }
        public decimal  MaterialPrice      { get; init; }
        public decimal  LaborPrice         { get; init; }
        public int      PriceType          { get; init; }
        public string?  SearchText         { get; init; }
        public bool     IsSelectable       { get; init; } = true;
        public bool     IsActive           { get; init; } = true;
        public bool     IsManuallyAdded    { get; init; }
        public bool     HasMissingUnit     { get; init; }
        public bool     IsCurrencyBased    { get; init; }
        public string?  CurrencyCode       { get; init; }
        public decimal? ListPriceUsd       { get; init; }
        public decimal? DiscountRate       { get; init; }
        public decimal? DiscountedUsdPrice { get; init; }
        public bool     ExchangeRateRequired { get; init; }
    }

    private record AliasSnapshot
    {
        public string?  Keyword           { get; init; }
        public string   NormalizedKeyword { get; init; } = "";
        public string?  TargetText        { get; init; }
        public bool     IsActive          { get; init; } = true;
    }
}
