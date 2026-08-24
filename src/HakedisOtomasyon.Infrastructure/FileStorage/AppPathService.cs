using HakedisOtomasyon.Application.Interfaces;

namespace HakedisOtomasyon.Infrastructure.FileStorage;

/// <summary>
/// Uygulamanın tüm klasör yollarını merkezi olarak yönetir.
/// Ana veri klasörü: Masaüstü\HAKEDİŞ DATABASE (veya kullanıcının AppData JSON'da seçtiği konum).
/// </summary>
public class AppPathService : IAppPathService
{
    private static readonly string[] TurkishMonths =
    [
        "Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran",
        "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"
    ];

    public string DataRootPath { get; private set; }
    public string AppRoot       => DataRootPath;
    public string DatabasePath  { get; private set; }
    public string DataPath      { get; private set; }
    public string HakedislerPath    { get; private set; }
    public string MasterDataPath    { get; private set; }
    public string StoreImportsPath  { get; private set; }
    public string StoresCatalogPath => StoreImportsPath;
    public string PriceCatalogImportsPath { get; private set; }
    public string PriceCatalogPath        => PriceCatalogImportsPath;
    public string BackupsPath { get; private set; }
    public string LogsPath    { get; private set; }
    public string TempPath    { get; private set; }
    public bool   DataRootValid { get; private set; }

    public AppPathService()
    {
        var config = AppDataSettings.Load();

        if (string.IsNullOrWhiteSpace(config.DataRootPath))
        {
            // İlk açılış: varsayılan masaüstü konumu
            DataRootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "HAKEDİŞ DATABASE");
            config.DataRootPath = DataRootPath;
            AppDataSettings.Save(config);
        }
        else
        {
            DataRootPath = config.DataRootPath;
        }

        BuildPaths();

        if (Directory.Exists(DataRootPath))
        {
            DataRootValid = true;
            EnsureFolders();
        }
        else
        {
            DataRootValid = false;
        }
    }

    // ─── Path hesaplama ──────────────────────────────────────────────────

    private void BuildPaths()
    {
        DatabasePath          = Path.Combine(DataRootPath, "Data", "hakedis.db");
        DataPath              = Path.Combine(DataRootPath, "Data");
        HakedislerPath        = Path.Combine(DataRootPath, "Hakedişler");
        MasterDataPath        = Path.Combine(DataRootPath, "MasterData");
        StoreImportsPath      = Path.Combine(MasterDataPath, "StoreImports");
        PriceCatalogImportsPath = Path.Combine(MasterDataPath, "PriceCatalogImports");
        BackupsPath           = Path.Combine(DataRootPath, "Backups");
        LogsPath              = Path.Combine(DataRootPath, "Logs");
        TempPath              = Path.Combine(DataRootPath, "Temp");
    }

    private void EnsureFolders()
    {
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(HakedislerPath);
        Directory.CreateDirectory(MasterDataPath);
        Directory.CreateDirectory(StoreImportsPath);
        Directory.CreateDirectory(PriceCatalogImportsPath);
        Directory.CreateDirectory(BackupsPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(TempPath);
    }

    // ─── Klasör yolu metodları ───────────────────────────────────────────

    public string GetMonthFolder(int year, int month)
    {
        var monthName = TurkishMonths[month - 1];
        return Path.Combine(HakedislerPath, year.ToString(), $"{month:00} - {monthName}");
    }

    public string GetServiceFormsFolder(int year, int month)
    {
        var path = Path.Combine(GetMonthFolder(year, month), "Servis Formları");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetInvoicesFolder(int year, int month)
    {
        var path = Path.Combine(GetMonthFolder(year, month), "Faturalar");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetExcelExportsFolder(int year, int month)
    {
        var path = Path.Combine(GetMonthFolder(year, month), "Excel Çıktıları");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetArchiveFolder(int year, int month)
    {
        var path = Path.Combine(GetMonthFolder(year, month), "Arşiv");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetArchiveServiceFormsFolder(int year, int month)
    {
        var path = Path.Combine(GetArchiveFolder(year, month), "Servis Formları");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetArchiveInvoicesFolder(int year, int month)
    {
        var path = Path.Combine(GetArchiveFolder(year, month), "Faturalar");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetArchiveExcelExportsFolder(int year, int month)
    {
        var path = Path.Combine(GetArchiveFolder(year, month), "Excel Çıktıları");
        Directory.CreateDirectory(path);
        return path;
    }

    // Geriye dönük uyumluluk takma adları
    public string GetServiceFormsPath(int year, int month) => GetServiceFormsFolder(year, month);
    public string GetInvoicesPath(int year, int month)     => GetInvoicesFolder(year, month);
    public string GetExportsPath(int year, int month)      => GetExcelExportsFolder(year, month);

    // ─── Path dönüşüm ───────────────────────────────────────────────────

    public string ToAbsolutePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
        return Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.GetFullPath(Path.Combine(DataRootPath, relativePath));
    }

    public string ToRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return string.Empty;
        if (!Path.IsPathRooted(absolutePath)) return absolutePath;
        return Path.GetRelativePath(DataRootPath, absolutePath);
    }

    // ─── Veri klasörü yönetimi ───────────────────────────────────────────

    public bool ValidateDataRoot()
    {
        DataRootValid = Directory.Exists(DataRootPath);
        return DataRootValid;
    }

    public async Task ChangeDataRootAsync(string newPath, bool moveExistingData)
    {
        var oldPath = DataRootPath;

        if (moveExistingData && Directory.Exists(oldPath) &&
            !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(newPath);
            await Task.Run(() => CopyDirectory(oldPath, newPath));
        }

        // Yeni yolu belleğe ve AppData JSON'a kaydet
        DataRootPath = newPath;
        BuildPaths();
        DataRootValid = Directory.Exists(DataRootPath);

        var config = AppDataSettings.Load();
        config.DataRootPath = newPath;
        AppDataSettings.Save(config);

        if (DataRootValid)
            EnsureFolders();
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var dest = dir.Replace(source, destination);
            Directory.CreateDirectory(dest);
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = file.Replace(source, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
