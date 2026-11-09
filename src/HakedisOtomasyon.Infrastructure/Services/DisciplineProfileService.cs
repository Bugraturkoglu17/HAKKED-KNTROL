using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Domain.Models;

namespace HakedisOtomasyon.Infrastructure.Services;

public class DisciplineProfileService : IDisciplineProfileService
{
    private readonly IAppPathService _appPath;

    /// <summary>
    /// Aşama 1'de kullanılan eski (artık terk edilmiş) veri kökü. Masaüstünde fazladan
    /// klasör oluşmasın diye kaldırıldı — içeriği varsa <see cref="EnsureFolderSkeleton"/>
    /// sırasında tek seferlik olarak gerçek veri köküne (HAKEDİŞ DATABASE) taşınır.
    /// </summary>
    private const string LegacyRootFolderName = "SERVİS OTOMASYONU DATABASE";

    private static readonly IReadOnlyList<DisciplineProfile> _profiles = new List<DisciplineProfile>
    {
        new() { Discipline = MechanicalDiscipline.Fire,     DisplayName = "Yangın",  RouteName = "yangin",  ThemeColor = "#B3261E", DataFolderName = "Yangin",  IsActive = true  },
        new() { Discipline = MechanicalDiscipline.Hvac,     DisplayName = "Klima",   RouteName = "klima",   ThemeColor = "#009688", DataFolderName = "Klima",   IsActive = true  },
        new() { Discipline = MechanicalDiscipline.Elevator, DisplayName = "Asansör", RouteName = "asansor", ThemeColor = "#5E35B1", DataFolderName = "Asansor", IsActive = true  },
        new() { Discipline = MechanicalDiscipline.Cooling,  DisplayName = "Soğutma", RouteName = "sogutma", ThemeColor = "#1976D2", DataFolderName = "Sogutma", IsActive = true  },
    };

    public DisciplineProfileService(IAppPathService appPath)
    {
        _appPath = appPath;
    }

    public IReadOnlyList<DisciplineProfile> GetAll() => _profiles;

    public DisciplineProfile? GetByRoute(string routeName) =>
        _profiles.FirstOrDefault(p => string.Equals(p.RouteName, routeName, StringComparison.OrdinalIgnoreCase));

    public void EnsureFolderSkeleton()
    {
        MigrateLegacyRootIfPresent();

        var root = _appPath.DataRootPath;

        foreach (var profile in _profiles)
        {
            var disciplineRoot = Path.Combine(root, "Disciplines", profile.DataFolderName);
            Directory.CreateDirectory(Path.Combine(disciplineRoot, "MasterData"));
            Directory.CreateDirectory(Path.Combine(disciplineRoot, "ReferenceTables"));
            Directory.CreateDirectory(Path.Combine(disciplineRoot, "Uploads", "ServiceForms"));
            Directory.CreateDirectory(Path.Combine(disciplineRoot, "Uploads", "Invoices"));
            Directory.CreateDirectory(Path.Combine(disciplineRoot, "Exports"));
            Directory.CreateDirectory(Path.Combine(disciplineRoot, "Hakedişler"));
            Directory.CreateDirectory(Path.Combine(disciplineRoot, "Archive"));
        }

        ExtractFireReferenceTableImages();
    }

    /// <summary>
    /// Eski "SERVİS OTOMASYONU DATABASE" klasörü masaüstünde varsa içeriğini gerçek
    /// veri köküne (HAKEDİŞ DATABASE) taşır. Hedefte aynı isimde dosya/klasör varsa
    /// ÜZERİNE YAZMAZ — kaynakta bırakır ve log dosyasına not eder. Taşıma sonrası
    /// kaynak klasör tamamen boşaldıysa otomatik silinir; boşalmadıysa olduğu gibi kalır.
    /// </summary>
    private void MigrateLegacyRootIfPresent()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var legacyRoot = Path.Combine(desktop, LegacyRootFolderName);
            if (!Directory.Exists(legacyRoot)) return;

            var moved = new List<string>();
            var leftBehind = new List<string>();

            var legacyDisciplines = Path.Combine(legacyRoot, "Disciplines");
            if (Directory.Exists(legacyDisciplines))
                MoveDirectoryContents(legacyDisciplines, Path.Combine(_appPath.DataRootPath, "Disciplines"), moved, leftBehind);

            var legacyLogs = Path.Combine(legacyRoot, "Common", "Logs");
            if (Directory.Exists(legacyLogs))
                MoveDirectoryContents(legacyLogs, _appPath.LogsPath, moved, leftBehind);

            var legacyBackups = Path.Combine(legacyRoot, "Common", "Backups");
            if (Directory.Exists(legacyBackups))
                MoveDirectoryContents(legacyBackups, _appPath.BackupsPath, moved, leftBehind);

            // Common\Data, Common\License gibi hiçbir zaman kullanılmamış boş klasörler
            // kalmış olabilir — içleri boşsa sessizce temizlenir.
            RemoveEmptyDirectoriesRecursive(legacyRoot);

            bool fullyEmpty = Directory.Exists(legacyRoot) &&
                !Directory.EnumerateFileSystemEntries(legacyRoot, "*", SearchOption.AllDirectories).Any();

            if (fullyEmpty)
                Directory.Delete(legacyRoot, recursive: true);

            WriteMigrationLog(moved, leftBehind, legacyRoot, removed: fullyEmpty);
        }
        catch
        {
            // Migration hatası uygulama açılışını engellemesin
        }
    }

    private static void MoveDirectoryContents(string sourceDir, string destDir, List<string> moved, List<string> leftBehind)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(destFile))
            {
                leftBehind.Add(file);
                continue;
            }
            try
            {
                File.Move(file, destFile);
                moved.Add(destFile);
            }
            catch
            {
                leftBehind.Add(file);
            }
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
            MoveDirectoryContents(dir, Path.Combine(destDir, Path.GetFileName(dir)), moved, leftBehind);
    }

    private static void RemoveEmptyDirectoriesRecursive(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir))
            RemoveEmptyDirectoriesRecursive(sub);

        if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
        {
            try { Directory.Delete(dir); } catch { /* ignore */ }
        }
    }

    private void WriteMigrationLog(List<string> moved, List<string> leftBehind, string legacyRoot, bool removed)
    {
        if (moved.Count == 0 && leftBehind.Count == 0) return;
        try
        {
            Directory.CreateDirectory(_appPath.LogsPath);
            var logFile = Path.Combine(_appPath.LogsPath, "legacy-folder-migration-log.txt");
            var lines = new List<string>
            {
                $"=== Eski klasör taşıma — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"Eski klasör: {legacyRoot}",
                $"Taşınan dosya sayısı: {moved.Count}",
                $"Geride kalan (hedefte zaten vardı) dosya sayısı: {leftBehind.Count}",
                $"Eski klasör silindi mi: {removed}",
            };
            if (leftBehind.Count > 0)
            {
                lines.Add("Geride kalan dosyalar (manuel kontrol edebilirsiniz):");
                lines.AddRange(leftBehind.Select(f => $"  {f}"));
            }
            lines.Add(string.Empty);
            File.AppendAllLines(logFile, lines);
        }
        catch
        {
            // Log yazılamazsa sessizce atla
        }
    }

    // BDKF/BDTX/Flexiva görselleri Excel export sırasında gömülü kaynaktan (EmbeddedResource)
    // okunur; kullanıcı bunları dosya olarak göremez. "Yangın Referans Tablolarını Aç" butonu
    // anlamlı olsun diye aynı görseller burada da fiziksel dosya olarak çıkarılır.
    private static readonly string[] FireReferenceImages =
    {
        "bdkf-table.png",
        "bdtx-table.png",
        "flexiva-table.png"
    };

    private void ExtractFireReferenceTableImages()
    {
        var targetDir = GetReferenceTablesPath(MechanicalDiscipline.Fire);
        Directory.CreateDirectory(targetDir);

        foreach (var fileName in FireReferenceImages)
        {
            try
            {
                var targetPath = Path.Combine(targetDir, fileName);
                // Kullanıcı bu dosyayı kendi tablosuyla değiştirmiş olabilir — ASLA üzerine yazma.
                if (File.Exists(targetPath)) continue;

                using var stream = GetEmbeddedResourceStream(fileName);
                if (stream is null) continue;

                using var fileStream = File.Create(targetPath);
                stream.CopyTo(fileStream);
            }
            catch
            {
                // Görsel çıkarılamazsa sessizce atla — uygulama açılışını engellemez
            }
        }
    }

    private static Stream? GetEmbeddedResourceStream(string fileName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? resourceName;
            try
            {
                resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }

            if (resourceName is null) continue;
            var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is not null) return stream;
        }
        return null;
    }

    private string GetProfileFolderName(MechanicalDiscipline discipline) =>
        _profiles.First(p => p.Discipline == discipline).DataFolderName;

    /// <summary>Tek ve gerçek veri kökü — Desktop\HAKEDİŞ DATABASE (veya kullanıcının seçtiği konum).</summary>
    public string GetRootPath() => _appPath.DataRootPath;

    /// <summary>
    /// Ayrı bir "Common" alt klasörü OLUŞTURMAZ — mevcut Logs/Backups/Data klasörleriyle
    /// aynı kökü kullanır. Örn. Path.Combine(GetCommonPath(), "Logs") == AppPath.LogsPath.
    /// </summary>
    public string GetCommonPath() => _appPath.DataRootPath;

    public string GetDisciplineRoot(MechanicalDiscipline discipline) =>
        Path.Combine(GetRootPath(), "Disciplines", GetProfileFolderName(discipline));

    public string GetMasterDataPath(MechanicalDiscipline discipline) =>
        Path.Combine(GetDisciplineRoot(discipline), "MasterData");

    public string GetUploadsPath(MechanicalDiscipline discipline) =>
        Path.Combine(GetDisciplineRoot(discipline), "Uploads");

    public string GetServiceFormsPath(MechanicalDiscipline discipline) =>
        Path.Combine(GetUploadsPath(discipline), "ServiceForms");

    public string GetInvoicesPath(MechanicalDiscipline discipline) =>
        Path.Combine(GetUploadsPath(discipline), "Invoices");

    public string GetExportsPath(MechanicalDiscipline discipline) =>
        Path.Combine(GetDisciplineRoot(discipline), "Exports");

    public string GetProgressClaimsPath(MechanicalDiscipline discipline) =>
        Path.Combine(GetDisciplineRoot(discipline), "Hakedişler");

    public string GetArchivePath(MechanicalDiscipline discipline) =>
        Path.Combine(GetDisciplineRoot(discipline), "Archive");

    public string GetReferenceTablesPath(MechanicalDiscipline discipline) =>
        Path.Combine(GetDisciplineRoot(discipline), "ReferenceTables");
}
