using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Geri yükleme marker dosyasını yönetir.
///
/// Akış:
/// 1. Kullanıcı "Tam Yedekten Geri Yükle" butonuna basar.
/// 2. ScheduleRestore() çağrılır → pending-restore.json yazılır.
/// 3. Uygulama kapanır.
/// 4. Sonraki açılışta App.xaml.cs → ExecutePendingRestoreIfAnyAsync() çağrılır.
/// 5. DbContext başlamadan önce hakedis.db yerine yedekteki db kopyalanır.
/// 6. Marker dosyası silinir, normal akış devam eder.
/// </summary>
public static class PendingRestoreService
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ServisHakedis",
        "pending-restore.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ─── Marker yaz ─────────────────────────────────────────────────────────

    /// <summary>
    /// Geri yüklemeyi sonraki başlatmaya erteler.
    /// </summary>
    public static void ScheduleRestore(string backupZipPath)
    {
        var marker = new PendingRestoreMarker
        {
            BackupZipPath = backupZipPath,
            CreatedAt = DateTime.UtcNow,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker, JsonOpts));
    }

    /// <summary>
    /// Bekleyen bir geri yükleme var mı?
    /// </summary>
    public static bool HasPendingRestore =>
        File.Exists(MarkerPath) && TryLoadMarker() is not null;

    // ─── Geri yüklemeyi uygula ───────────────────────────────────────────────

    /// <summary>
    /// Marker varsa geri yüklemeyi uygular. DbContext başlamadan ÖNCE çağrılmalı.
    /// </summary>
    /// <param name="activeDbPath">Çalışma zamanındaki hakedis.db yolu (AppPathService.DatabasePath)</param>
    /// <param name="dataRootPath">Veri kök klasörü (AppPathService.DataRootPath)</param>
    /// <returns>Geri yükleme yapıldıysa true</returns>
    public static bool ExecutePendingRestoreIfAny(string activeDbPath, string dataRootPath)
    {
        var marker = TryLoadMarker();
        if (marker is null) return false;

        try
        {
            if (!File.Exists(marker.BackupZipPath))
            {
                // ZIP artık yok, marker'ı temizle
                DeleteMarker();
                return false;
            }

            using var zip = ZipFile.OpenRead(marker.BackupZipPath);

            // ─── 1. hakedis.db'yi geri yükle ───────────────────────────────
            var dbEntry = zip.GetEntry("Data/hakedis.db")
                        ?? zip.GetEntry("hakedis.db");

            if (dbEntry is not null)
            {
                // Arşiv: mevcut db'yi yedekle
                if (File.Exists(activeDbPath))
                {
                    var archiveDir = Path.Combine(dataRootPath, "Backups", "RestoreArchive");
                    Directory.CreateDirectory(archiveDir);
                    var archiveName = $"hakedis_before_restore_{DateTime.Now:yyyyMMdd_HHmmss}.db";
                    File.Copy(activeDbPath, Path.Combine(archiveDir, archiveName), overwrite: true);
                    File.Delete(activeDbPath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(activeDbPath)!);
                dbEntry.ExtractToFile(activeDbPath, overwrite: true);
            }

            // ─── 2. Diğer klasörleri geri yükle ────────────────────────────
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // klasör girdisi
                if (entry.FullName == "Data/hakedis.db" || entry.FullName == "hakedis.db") continue; // zaten işlendi

                string? targetPath = null;

                if (entry.FullName.StartsWith("Hakedişler/", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = entry.FullName[("Hakedişler/".Length)..].Replace('/', Path.DirectorySeparatorChar);
                    targetPath = Path.Combine(dataRootPath, "Hakedişler", rel);
                }
                else if (entry.FullName.StartsWith("MasterData/", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = entry.FullName[("MasterData/".Length)..].Replace('/', Path.DirectorySeparatorChar);
                    targetPath = Path.Combine(dataRootPath, "MasterData", rel);
                }
                // Eski yedek formatı uyumu
                else if (entry.FullName.StartsWith("Uploads/", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = entry.FullName[8..].Replace('/', Path.DirectorySeparatorChar);
                    targetPath = Path.Combine(dataRootPath, "Hakedişler", rel);
                }

                if (targetPath is null) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }

            DeleteMarker();
            return true;
        }
        catch
        {
            // Geri yükleme başarısız olursa marker'ı sil, normal açılış devam etsin
            DeleteMarker();
            return false;
        }
    }

    // ─── Yardımcı ───────────────────────────────────────────────────────────

    private static PendingRestoreMarker? TryLoadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var json = File.ReadAllText(MarkerPath);
            return JsonSerializer.Deserialize<PendingRestoreMarker>(json, JsonOpts);
        }
        catch { return null; }
    }

    private static void DeleteMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); }
        catch { /* yoksay */ }
    }
}

internal sealed class PendingRestoreMarker
{
    public string BackupZipPath { get; set; } = string.Empty;
    public DateTime CreatedAt   { get; set; }
}
