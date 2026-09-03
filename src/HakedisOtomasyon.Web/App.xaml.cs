using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Infrastructure.Data;
using HakedisOtomasyon.Infrastructure.DependencyInjection;
using HakedisOtomasyon.Infrastructure.FileStorage;
using HakedisOtomasyon.Infrastructure.Services;
using HakedisOtomasyon.Web.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MudBlazor.Services;
using QuestPDF.Infrastructure;
using SogutmaHakedisKontrol.Infrastructure.DependencyInjection;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace HakedisOtomasyon.Web;

public partial class WpfApp : System.Windows.Application
{
    public IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hata yakalayıcılar
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // ─── 0. Lisans kontrolü ────────────────────────────────────────────
        if (!LicenseService.IsLicenseValid())
        {
            var licenseWindow = new LicenseActivationWindow();
            bool? activated = licenseWindow.ShowDialog();

            if (activated != true || !licenseWindow.ActivationSucceeded)
            {
                Shutdown();
                return;
            }
        }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // QuestPDF lisansı
        QuestPDF.Settings.License = LicenseType.Community;

        // ─── 1. Veri klasörü doğrulama ─────────────────────────────────────
        // AppPathService'i DI'dan önce oluştur — DataRootPath AppData JSON'dan okunur.
        var appPathService = new AppPathService();

        if (!appPathService.DataRootValid)
        {
            bool resolved = ShowDataRootMissingDialog(appPathService);
            if (!resolved)
            {
                Shutdown();
                return;
            }
        }

        // ─── 1b. Bekleyen geri yükleme kontrolü ────────────────────────────
        // DbContext AÇILMADAN önce yapılmalı, aksi hâlde hakedis.db kilitlenir.
        bool restoredFromPending = false;
        if (PendingRestoreService.HasPendingRestore)
        {
            restoredFromPending = PendingRestoreService.ExecutePendingRestoreIfAny(
                appPathService.DatabasePath,
                appPathService.DataRootPath);
        }

        // ─── 2. Yapılandırma ───────────────────────────────────────────────
        var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        // ─── 3. Servis kaydı ───────────────────────────────────────────────
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();

#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        services.AddMudServices(cfg =>
        {
            cfg.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
            cfg.SnackbarConfiguration.PreventDuplicates = false;
            cfg.SnackbarConfiguration.NewestOnTop = true;
            cfg.SnackbarConfiguration.ShowCloseIcon = true;
            cfg.SnackbarConfiguration.VisibleStateDuration = 3500;
            cfg.SnackbarConfiguration.HideTransitionDuration = 300;
            cfg.SnackbarConfiguration.ShowTransitionDuration = 300;
        });

        services.AddSingleton<IConfiguration>(config);
        // appPathService önceden oluşturuldu; doğrulanmış DB yolunu kullan
        services.AddApplicationServices(config, appPathService);

        // Soğutma Hakediş Kontrolü modülü — ayrı DbContext/servisler, kendi veri kökü
        services.AddSogutmaHakedisKontrolServices();

        // Native dosya seçici
        services.AddSingleton<IFilePickerService, WpfFilePickerService>();
        services.AddSingleton<SogutmaHakedisKontrol.Application.Interfaces.IFilePickerService, SogutmaHakedisKontrol.Web.WpfFilePickerService>();

        Services = services.BuildServiceProvider();
        Resources["services"] = Services;

        // Disiplin klasör iskeletini hazırla (Desktop\HAKEDİŞ DATABASE\Disciplines\...).
        // ÖNEMLİ: Bu, IPosMappingService'ten ÖNCE çalışmalı — aksi halde POS servisi
        // eski "SERVİS OTOMASYONU DATABASE" klasörü taşınmadan yeni konumda boş bir
        // POS dosyası oluşturur ve gerçek (eski) POS dosyasının taşınmasını engeller.
        // Hata olsa bile uygulama başlangıcını engellemez.
        try
        {
            Services.GetRequiredService<IDisciplineProfileService>().EnsureFolderSkeleton();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Disiplin klasör iskeleti oluşturulamadı: {ex.Message}");
        }

        // POS servisini startup aninda baslat: MasterData klasorune sablon dosyasini kopyalar.
        _ = Services.GetRequiredService<IPosMappingService>();

        // Pending restore flag'ini DI servisine aktar
        if (restoredFromPending)
        {
            var recoveryState = Services.GetRequiredService<RecoveryStateService>();
            recoveryState.RestoredFromPendingBackup = true;
        }

        // Veritabanını başlat
        try
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            MigratePriceItemsSchema(db);
            MigrateProgressClaimSchema(db);
            MigratePriceAliasesSchema(db);
            MigrateServiceItemsSchema(db);
            MigrateServiceItemsCurrencySchema(db);
            MigrateExchangeRateCacheSchema(db);
            MigratePriceItemsCurrencySchema(db);
            MigrateArchiveSchema(db);
            MigrateProgressClaimTypeSchema(db);
            MigrateDisciplineSchema(db);
            MigrateStoreDisciplineIndexSchema(db);
            MigratePriceItemNotesSchema(db);

            // ─── Soğutma Hakediş Kontrolü modülü — ayrı veritabanı ─────────────
            try
            {
                var sogutmaPath = Services.GetRequiredService<SogutmaHakedisKontrol.Application.Interfaces.IAppPathService>();
                SeedSogutmaKontrolDatabaseIfMissing(sogutmaPath.DatabasePath);
                var sogutmaDb = scope.ServiceProvider.GetRequiredService<SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext>();
                sogutmaDb.Database.EnsureCreated();
                MigrateSogutmaAiSchema(sogutmaDb);
                MigrateSogutmaPriceCorrectionSchema(sogutmaDb);
                MigrateSogutmaCategorySchema(sogutmaDb);
                MigrateSogutmaFormNumberSchema(sogutmaDb);
                MigrateSogutmaOverrideSchema(sogutmaDb);
                MigrateSogutmaGlycolSecondarySchema(sogutmaDb);
                MigrateSogutmaPageCorrectionSchema(sogutmaDb);
            }
            catch (Exception ex)
            {
                LogError("Soğutma Hakediş Kontrolü veritabanı başlatma hatası", ex);
            }

            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            await settingsService.EnsureDefaultSettingsAsync();

            // ─── Kurtarma kontrolü ─────────────────────────────────────────
            // Tablolar boşsa (yeni/bozuk DB) → korunan yedekten otomatik geri yükle
            // veya manuel kurtarma seçeneğini göster.
            try
            {
                var recovery = scope.ServiceProvider.GetRequiredService<IDataRecoveryService>();
                var recoveryState = Services.GetRequiredService<RecoveryStateService>();
                if (await recovery.TablesAreEmptyAsync())
                {
                    if (recovery.HasProtectedSnapshot)
                    {
                        // Otomatik geri yükleme — sessiz, kullanıcıya bildirim MainLayout'ta gösterilir
                        var restoreResult = await recovery.RestoreFromProtectedDataAsync(true, true, true);
                        if (restoreResult.TotalRestored > 0)
                            recoveryState.AutoRestoreResult = restoreResult;
                        else
                        {
                            // Geri yükleme sıfır satır döndürdü, manuel taramaya geç
                            var scanResult = await recovery.ScanForRecoverySourcesAsync();
                            if (scanResult.HasAnySource)
                            {
                                recoveryState.RecoveryNeeded = true;
                                recoveryState.ScanResult     = scanResult;
                            }
                        }
                    }
                    else
                    {
                        var scanResult = await recovery.ScanForRecoverySourcesAsync();
                        if (scanResult.HasAnySource)
                        {
                            recoveryState.RecoveryNeeded = true;
                            recoveryState.ScanResult     = scanResult;
                        }
                    }
                }
            }
            catch { /* Kurtarma hatası başlatmayı engellemez */ }
        }
        catch (Exception ex)
        {
            LogError("Veritabanı başlatma hatası", ex);
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();

        CreateDesktopShortcutIfNeeded();

        // Arşiv başlangıç temizliği (sessiz, arka planda)
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = Services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IFileCleanupService>();
                await svc.RunStartupCleanupAsync();
            }
            catch { /* sessizce yoksay */ }
        });
    }

    /// <summary>
    /// Soğutma Hakediş Kontrolü modülü için ilk çalıştırmada veritabanı yoksa,
    /// uygulamayla birlikte dağıtılan onaylı birim fiyat kataloğunu içeren tohum
    /// veritabanını kopyalar. Kullanıcının kendi verisi asla üzerine yazılmaz.
    /// </summary>
    private static void SeedSogutmaKontrolDatabaseIfMissing(string databasePath)
    {
        if (File.Exists(databasePath)) return;

        var seedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SeedData", "sogutma_hakedis_seed.db");
        if (!File.Exists(seedPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.Copy(seedPath, databasePath);
    }

    /// <summary>
    /// Soğutma Hakediş Kontrolü DB'si daha önceki bir sürümde (AI belge analizi/mağaza ana
    /// listesi eklenmeden önce) oluşturulmuşsa, EnsureCreated() var olan dosyaya yeni tablo/sütun
    /// eklemez. Bu yüzden ProgressPaymentCheckItems.MatchedStoreId sütununu ve Store/AiAnalysisJob/
    /// AiDocumentPage/AiPageEmployee/AiPageMaterial/AiComparisonResult/AiUsageLog tablolarını,
    /// eksikse, EF modelinden üretilen DDL'i kullanarak (mevcut veriye dokunmadan) ekler.
    /// </summary>
    private static void MigrateSogutmaAiSchema(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) existingTables.Add(reader.GetString(0));
                }

                // ProgressPaymentCheckItems.MatchedStoreId (mevcut tabloya eklenen tekil sütun)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(ProgressPaymentCheckItems)";
                    var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) cols.Add(reader.GetString(1));
                    if (!cols.Contains("MatchedStoreId"))
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText = "ALTER TABLE \"ProgressPaymentCheckItems\" ADD COLUMN \"MatchedStoreId\" INTEGER";
                        alter.ExecuteNonQuery();
                    }
                }

                // Mağaza ana listesi + AI belge analizi tabloları (tamamen yeni tablolar)
                var newTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Stores", "AiAnalysisJobs", "AiDocumentPages", "AiPageEmployees",
                    "AiPageMaterials", "AiComparisonResults", "AiUsageLogs", "AiSourceDocuments",
                };
                if (newTableNames.Any(t => !existingTables.Contains(t)))
                {
                    var script = db.Database.GenerateCreateScript();
                    var statements = script
                        .Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0);

                    foreach (var stmt in statements)
                    {
                        var tableMatch = System.Text.RegularExpressions.Regex.Match(stmt, "CREATE TABLE \"(\\w+)\"");
                        if (tableMatch.Success)
                        {
                            var tableName = tableMatch.Groups[1].Value;
                            if (!newTableNames.Contains(tableName) || existingTables.Contains(tableName)) continue;
                            using var create = conn.CreateCommand();
                            create.CommandText = stmt + ";";
                            create.ExecuteNonQuery();
                            continue;
                        }

                        var indexMatch = System.Text.RegularExpressions.Regex.Match(stmt, "CREATE (?:UNIQUE )?INDEX \"[^\"]+\" ON \"(\\w+)\"");
                        if (indexMatch.Success)
                        {
                            var tableName = indexMatch.Groups[1].Value;
                            if (!newTableNames.Contains(tableName) || existingTables.Contains(tableName)) continue;
                            using var idx = conn.CreateCommand();
                            idx.CommandText = stmt + ";";
                            idx.ExecuteNonQuery();
                        }
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch (Exception ex)
        {
            LogError("Soğutma AI şema migrasyonu hatası", ex);
        }
    }

    /// <summary>
    /// Birim fiyat düzeltme özelliği (Düzelt/Geri Al/Yeni Kalem Ekle/Toplu Düzelt) için gereken
    /// ProgressPaymentCheckItems sütunlarını ve CheckItemActionLogs denetim izi tablosunu, eksikse,
    /// mevcut veriye dokunmadan ekler.
    /// </summary>
    private static void MigrateSogutmaPriceCorrectionSchema(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) existingTables.Add(reader.GetString(0));
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(ProgressPaymentCheckItems)";
                    var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) cols.Add(reader.GetString(1));

                    var newColumns = new Dictionary<string, string>
                    {
                        ["MaterialCellRef"] = "TEXT",
                        ["QuantityCellRef"] = "TEXT",
                        ["UnitPriceCellRef"] = "TEXT",
                        ["LineTotalCellRef"] = "TEXT",
                        ["PriceCorrectionApplied"] = "INTEGER NOT NULL DEFAULT 0",
                        // İLAVE İŞLER: Excel'deki "Mağazalar" master sayfasından (İşyeri No → IlAdi)
                        // mağaza koduyla eşleştirilerek doldurulur — şehir içi/şehir dışı servis bedeli
                        // türünün Excel'e göre doğrulanması için (bkz. AdditionalWorkComparisonStrategy).
                        ["StoreCity"] = "TEXT",
                    };
                    foreach (var (colName, colType) in newColumns)
                    {
                        if (cols.Contains(colName)) continue;
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE \"ProgressPaymentCheckItems\" ADD COLUMN \"{colName}\" {colType}";
                        alter.ExecuteNonQuery();
                    }
                }

                if (!existingTables.Contains("CheckItemActionLogs"))
                {
                    var script = db.Database.GenerateCreateScript();
                    var statements = script
                        .Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0);

                    foreach (var stmt in statements)
                    {
                        var tableMatch = System.Text.RegularExpressions.Regex.Match(stmt, "CREATE TABLE \"(\\w+)\"");
                        if (tableMatch.Success && tableMatch.Groups[1].Value == "CheckItemActionLogs")
                        {
                            using var create = conn.CreateCommand();
                            create.CommandText = stmt + ";";
                            create.ExecuteNonQuery();
                            continue;
                        }
                        var indexMatch = System.Text.RegularExpressions.Regex.Match(stmt, "CREATE (?:UNIQUE )?INDEX \"[^\"]+\" ON \"(\\w+)\"");
                        if (indexMatch.Success && indexMatch.Groups[1].Value == "CheckItemActionLogs")
                        {
                            using var idx = conn.CreateCommand();
                            idx.CommandText = stmt + ";";
                            idx.ExecuteNonQuery();
                        }
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch (Exception ex)
        {
            LogError("Soğutma fiyat düzeltme şema migrasyonu hatası", ex);
        }
    }

    /// <summary>
    /// Kategori bazlı çok aşamalı akış için ProgressPaymentChecks tablosuna Category/Stage sütunlarını
    /// ekler (eksikse). Eski kayıtlar Category=null, Stage=0 (CategorySelected) alır — bu satırlar hâlâ
    /// eski tek-sayfalı davranışıyla /discipline/sogutma/kontrol/{Id}'de görünmeye devam eder.
    /// </summary>
    private static void MigrateSogutmaCategorySchema(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(ProgressPaymentChecks)";
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read()) cols.Add(reader.GetString(1));

                var newColumns = new Dictionary<string, string>
                {
                    ["Category"] = "INTEGER",
                    ["Stage"] = "INTEGER NOT NULL DEFAULT 0",
                };
                foreach (var (colName, colType) in newColumns)
                {
                    if (cols.Contains(colName)) continue;
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"ProgressPaymentChecks\" ADD COLUMN \"{colName}\" {colType}";
                    alter.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch (Exception ex)
        {
            LogError("Soğutma kategori şema migrasyonu hatası", ex);
        }
    }

    /// <summary>
    /// Form numarası bazlı eşleştirme için AiDocumentPages.FormNumberConfidence sütununu (eksikse) ekler.
    /// </summary>
    private static void MigrateSogutmaFormNumberSchema(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(AiDocumentPages)";
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read()) cols.Add(reader.GetString(1));

                if (!cols.Contains("FormNumberConfidence"))
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = "ALTER TABLE \"AiDocumentPages\" ADD COLUMN \"FormNumberConfidence\" TEXT";
                    alter.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch (Exception ex)
        {
            LogError("Soğutma form numarası şema migrasyonu hatası", ex);
        }
    }

    /// <summary>
    /// Manuel onay/geri al özelliği: AiComparisonResults'a UserOverridden/OriginalStatus/OverrideNote
    /// sütunlarını, AiComparisonOverrides'ı (tamamen yeni tablo, kalıcı onay kayıtları) ekler.
    /// </summary>
    private static void MigrateSogutmaOverrideSchema(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) existingTables.Add(reader.GetString(0));
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(AiComparisonResults)";
                    var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) cols.Add(reader.GetString(1));

                    var newColumns = new Dictionary<string, string>
                    {
                        ["UserOverridden"] = "INTEGER NOT NULL DEFAULT 0",
                        ["OriginalStatus"] = "INTEGER",
                        ["OverrideNote"] = "TEXT",
                    };
                    foreach (var (colName, colType) in newColumns)
                    {
                        if (cols.Contains(colName)) continue;
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE \"AiComparisonResults\" ADD COLUMN \"{colName}\" {colType}";
                        alter.ExecuteNonQuery();
                    }
                }

                if (!existingTables.Contains("AiComparisonOverrides"))
                {
                    var script = db.Database.GenerateCreateScript();
                    var statements = script
                        .Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0);

                    foreach (var stmt in statements)
                    {
                        var tableMatch = System.Text.RegularExpressions.Regex.Match(stmt, "CREATE TABLE \"(\\w+)\"");
                        if (tableMatch.Success)
                        {
                            if (tableMatch.Groups[1].Value != "AiComparisonOverrides") continue;
                            using var create = conn.CreateCommand();
                            create.CommandText = stmt + ";";
                            create.ExecuteNonQuery();
                            continue;
                        }

                        var indexMatch = System.Text.RegularExpressions.Regex.Match(stmt, "CREATE (?:UNIQUE )?INDEX \"[^\"]+\" ON \"(\\w+)\"");
                        if (indexMatch.Success)
                        {
                            if (indexMatch.Groups[1].Value != "AiComparisonOverrides") continue;
                            using var idx = conn.CreateCommand();
                            idx.CommandText = stmt + ";";
                            idx.ExecuteNonQuery();
                        }
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch (Exception ex)
        {
            LogError("Soğutma manuel onay şema migrasyonu hatası", ex);
        }
    }

    /// <summary>
    /// Tek kalemli kategorilerde (Glikol/Gaz Kullanım) Mağaza/Tarih uyuşmazlığı olan bir satırda da
    /// asıl miktar karşılaştırmasını (bkz. GlycolUsageComparisonStrategy) aynı satırda taşıyabilmek
    /// için AiComparisonResults'a eklenen ikincil alanlar.
    /// </summary>
    private static void MigrateSogutmaGlycolSecondarySchema(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(AiComparisonResults)";
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read()) cols.Add(reader.GetString(1));

                var newColumns = new Dictionary<string, string>
                {
                    ["SecondaryFormValue"] = "TEXT",
                    ["SecondaryHakedisValue"] = "TEXT",
                    ["SecondaryStatus"] = "INTEGER",
                    // İLAVE İŞLER: Material satırlarında "Düzelt" butonunun hangi AiPageMaterial'i
                    // düzelteceğini bilmesi için (bkz. AiComparisonResult.MatchedMaterialId).
                    ["MatchedMaterialId"] = "INTEGER",
                };
                foreach (var (colName, colType) in newColumns)
                {
                    if (cols.Contains(colName)) continue;
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"AiComparisonResults\" ADD COLUMN \"{colName}\" {colType}";
                    alter.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch (Exception ex)
        {
            LogError("Soğutma glikol ikincil alan şema migrasyonu hatası", ex);
        }
    }

    /// <summary>Adam-Saat ve Mağaza Uyuşmazlığı satırlarında kullanıcının "formdan okuduğum değer bu"
    /// düzeltmesini kaydedebilmesi için AiDocumentPages tablosuna yeni sütunlar ekler.</summary>
    private static void MigrateSogutmaPageCorrectionSchema(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(AiDocumentPages)";
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read()) cols.Add(reader.GetString(1));

                var newColumns = new Dictionary<string, string>
                {
                    ["UserCorrectedPayableManHours"] = "TEXT",
                    ["UserCorrectedManHoursNote"] = "TEXT",
                    ["UserCorrectedManHoursAt"] = "TEXT",
                    ["UserCorrectedStoreRaw"] = "TEXT",
                    ["UserCorrectedStoreNote"] = "TEXT",
                    ["UserCorrectedStoreAt"] = "TEXT",
                };
                foreach (var (colName, colType) in newColumns)
                {
                    if (cols.Contains(colName)) continue;
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"AiDocumentPages\" ADD COLUMN \"{colName}\" {colType}";
                    alter.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch (Exception ex)
        {
            LogError("Soğutma sayfa düzeltme şema migrasyonu hatası", ex);
        }
    }

    /// <summary>
    /// Mevcut SQLite DB'ye yeni PriceItems sütunlarını ekler (EnsureCreated ile uyumlu).
    /// </summary>
    private static void MigratePriceItemsSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(PriceItems)";
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        existingCols.Add(reader.GetString(1));

                var newColumns = new Dictionary<string, string>
                {
                    ["SourceSheetName"] = "TEXT",
                    ["SourceRowNumber"] = "INTEGER",
                    ["PozNo"] = "TEXT",
                    ["MainCategory"] = "TEXT",
                    ["SubCategory"] = "TEXT",
                    ["SubCategory2"] = "TEXT",
                    ["DisplayName"] = "TEXT",
                    ["InvoiceDescription"] = "TEXT",
                    ["PriceType"] = "INTEGER NOT NULL DEFAULT 0",
                    ["SearchText"] = "TEXT",
                    ["IsSelectable"] = "INTEGER NOT NULL DEFAULT 1",
                    ["IsManuallyAdded"] = "INTEGER NOT NULL DEFAULT 0",
                    ["HasMissingUnit"] = "INTEGER NOT NULL DEFAULT 0",
                    ["UpdatedAt"] = "TEXT",
                };

                foreach (var (col, type) in newColumns)
                {
                    if (!existingCols.Contains(col))
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE PriceItems ADD COLUMN \"{col}\" {type}";
                        alter.ExecuteNonQuery();
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    private static void MigratePriceAliasesSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                // PriceAliases tablosunu oluştur (yoksa)
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""PriceAliases"" (
                        ""Id""                INTEGER NOT NULL CONSTRAINT ""PK_PriceAliases"" PRIMARY KEY AUTOINCREMENT,
                        ""Keyword""           TEXT NOT NULL,
                        ""NormalizedKeyword"" TEXT NOT NULL,
                        ""TargetText""        TEXT NOT NULL,
                        ""IsActive""          INTEGER NOT NULL DEFAULT 1,
                        ""CreatedAt""         TEXT NOT NULL
                    )";
                cmd.ExecuteNonQuery();

                using var idx = conn.CreateCommand();
                idx.CommandText = @"
                    CREATE UNIQUE INDEX IF NOT EXISTS
                    ""IX_PriceAliases_NormalizedKeyword""
                    ON ""PriceAliases"" (""NormalizedKeyword"")";
                idx.ExecuteNonQuery();
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    private static void MigrateProgressClaimSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(ProgressClaims)";
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        existingCols.Add(reader.GetString(1));

                var newColumns = new Dictionary<string, string>
                {
                    ["CurrentStep"] = "INTEGER NOT NULL DEFAULT 0",
                    ["LastOpenedServiceFormId"] = "INTEGER",
                };

                foreach (var (col, type) in newColumns)
                {
                    if (!existingCols.Contains(col))
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE ProgressClaims ADD COLUMN \"{col}\" {type}";
                        alter.ExecuteNonQuery();
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    private static void MigrateServiceItemsSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(ServiceItems)";
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        existingCols.Add(reader.GetString(1));

                var newColumns = new Dictionary<string, string>
                {
                    ["IsManualEntry"]      = "INTEGER NOT NULL DEFAULT 0",
                    ["IsAutoGenerated"]    = "INTEGER NOT NULL DEFAULT 0",
                    ["AutoGeneratedType"]  = "TEXT",
                    ["AutoRuleTrigger"]    = "TEXT",
                    ["ExportDescription"]  = "TEXT",
                    ["ItemKey"]            = "TEXT NOT NULL DEFAULT ''",
                    ["ParentItemKey"]      = "TEXT",
                };

                foreach (var (col, type) in newColumns)
                {
                    if (!existingCols.Contains(col))
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE ServiceItems ADD COLUMN \"{col}\" {type}";
                        alter.ExecuteNonQuery();
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    private static void MigrateServiceItemsCurrencySchema(AppDbContext db)    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(ServiceItems)";
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        existingCols.Add(reader.GetString(1));

                var newColumns = new Dictionary<string, string>
                {
                    ["IsCurrencyBased"]        = "INTEGER NOT NULL DEFAULT 0",
                    ["CurrencyCode"]           = "TEXT",
                    ["ListPriceUsd"]           = "TEXT",
                    ["DiscountRate"]           = "TEXT",
                    ["DiscountedUsdPrice"]     = "TEXT",
                    ["ExchangeRate"]           = "TEXT",
                    ["ExchangeRateDate"]       = "TEXT",
                    ["ExchangeRateActualDate"] = "TEXT",
                    ["ExchangeRateSource"]     = "TEXT",
                };

                foreach (var (col, type) in newColumns)
                {
                    if (!existingCols.Contains(col))
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText = $"ALTER TABLE ServiceItems ADD COLUMN \"{col}\" {type}";
                        alter.ExecuteNonQuery();
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    private static void MigrateExchangeRateCacheSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ""ExchangeRateCaches"" (
                        ""Id""           INTEGER NOT NULL CONSTRAINT ""PK_ExchangeRateCaches"" PRIMARY KEY AUTOINCREMENT,
                        ""CurrencyCode"" TEXT NOT NULL DEFAULT 'USD',
                        ""RateDate""     TEXT NOT NULL,
                        ""ForexBuying""  TEXT NOT NULL DEFAULT '0',
                        ""ForexSelling"" TEXT NOT NULL DEFAULT '0',
                        ""Source""       TEXT NOT NULL DEFAULT 'TCMB',
                        ""CreatedAt""    TEXT NOT NULL,
                        ""UpdatedAt""    TEXT NOT NULL
                    )";
                cmd.ExecuteNonQuery();

                using var idx = conn.CreateCommand();
                idx.CommandText = @"
                    CREATE UNIQUE INDEX IF NOT EXISTS
                    ""IX_ExchangeRateCaches_CurrencyCode_RateDate""
                    ON ""ExchangeRateCaches"" (""CurrencyCode"", ""RateDate"")";
                idx.ExecuteNonQuery();
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    private static void MigratePriceItemsCurrencySchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                // Mevcut sütunları öğren
                using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA table_info(PriceItems)";
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var rdr = pragma.ExecuteReader())
                    while (rdr.Read())
                        existing.Add(rdr.GetString(1));

                var newCols = new Dictionary<string, string>
                {
                    ["IsCurrencyBased"]      = "INTEGER NOT NULL DEFAULT 0",
                    ["CurrencyCode"]         = "TEXT",
                    ["ListPriceUsd"]         = "TEXT",
                    ["DiscountRate"]         = "TEXT",
                    ["DiscountedUsdPrice"]   = "TEXT",
                    ["ExchangeRateRequired"] = "INTEGER NOT NULL DEFAULT 0",
                };

                foreach (var (col, def) in newCols)
                {
                    if (existing.Contains(col)) continue;
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"PriceItems\" ADD COLUMN \"{col}\" {def}";
                    alter.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    /// <summary>
    /// ServiceForms, Invoices, ExportLogs tablolarına arşiv/hash sütunlarını ekler.
    /// </summary>
    private static void MigrateArchiveSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                var tables = new Dictionary<string, Dictionary<string, string>>
                {
                    ["ServiceForms"] = new()
                    {
                        ["FileHash"]    = "TEXT",
                        ["FileSize"]    = "INTEGER NOT NULL DEFAULT 0",
                        ["IsArchived"]  = "INTEGER NOT NULL DEFAULT 0",
                        ["ArchivedAt"]  = "TEXT"
                    },
                    ["Invoices"] = new()
                    {
                        ["FileHash"]    = "TEXT",
                        ["FileSize"]    = "INTEGER NOT NULL DEFAULT 0",
                        ["IsArchived"]  = "INTEGER NOT NULL DEFAULT 0",
                        ["ArchivedAt"]  = "TEXT"
                    },
                    ["ExportLogs"] = new()
                    {
                        ["IsActive"]    = "INTEGER NOT NULL DEFAULT 1",
                        ["IsArchived"]  = "INTEGER NOT NULL DEFAULT 0",
                        ["ArchivedAt"]  = "TEXT"
                    }
                };

                foreach (var (table, columns) in tables)
                {
                    // Mevcut sütunları al
                    using var infoCmd = conn.CreateCommand();
                    infoCmd.CommandText = $"PRAGMA table_info({table})";
                    var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = infoCmd.ExecuteReader())
                        while (reader.Read())
                            existingCols.Add(reader.GetString(1));

                    foreach (var (col, colType) in columns)
                    {
                        if (!existingCols.Contains(col))
                        {
                            using var alter = conn.CreateCommand();
                            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {col} {colType}";
                            alter.ExecuteNonQuery();
                        }
                    }
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    /// <summary>
    /// ProgressClaims tablosuna ClaimType sütunu ekler.
    /// </summary>
    private static void MigrateProgressClaimTypeSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(ProgressClaims)";
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        existingCols.Add(reader.GetString(1));

                if (!existingCols.Contains("ClaimType"))
                {
                    using var alter = conn.CreateCommand();
                    // 0 = MaintenanceRepair (varsayılan — mevcut tüm kayıtlar Bakım/Onarım sayılır)
                    alter.CommandText = "ALTER TABLE ProgressClaims ADD COLUMN \"ClaimType\" INTEGER NOT NULL DEFAULT 0";
                    alter.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    /// <summary>
    /// Aşama 5: Disiplin bazlı veri izolasyonu için ProgressClaims/PriceItems/Stores
    /// tablolarına Discipline kolonu ekler. DEFAULT 0 = MechanicalDiscipline.Fire,
    /// yani tüm mevcut (eski) kayıtlar otomatik olarak Yangın'a ait sayılır.
    /// </summary>
    private static void MigrateDisciplineSchema(AppDbContext db)
    {
        foreach (var table in new[] { "ProgressClaims", "PriceItems", "Stores" })
        {
            try
            {
                var conn = db.Database.GetDbConnection();
                conn.Open();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"PRAGMA table_info({table})";
                    var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                            existingCols.Add(reader.GetString(1));

                    if (!existingCols.Contains("Discipline"))
                    {
                        using var alter = conn.CreateCommand();
                        // 0 = MechanicalDiscipline.Fire (varsayılan — eski kayıtlar Yangın'a ait sayılır)
                        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN \"Discipline\" INTEGER NOT NULL DEFAULT 0";
                        alter.ExecuteNonQuery();
                    }
                }
                finally { conn.Close(); }
            }
            catch { /* Migration hatası uygulamayı durdurmasın */ }
        }
    }

    /// <summary>
    /// Eski global "Stores.Code" unique index'ini kaldırıp (Discipline, Code) composite
    /// unique index ile değiştirir. Bu olmadan Yangın'da kayıtlı bir mağaza koduyla
    /// Klima/Asansör/Soğutma'da AYNI kodu kullanmaya çalışmak "UNIQUE constraint failed"
    /// hatasına yol açıyordu (mağaza ekleme bug'ı — kök sebep).
    /// </summary>
    private static void MigrateStoreDisciplineIndexSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using (var drop = conn.CreateCommand())
                {
                    drop.CommandText = "DROP INDEX IF EXISTS \"IX_Stores_Code\"";
                    drop.ExecuteNonQuery();
                }
                using (var create = conn.CreateCommand())
                {
                    create.CommandText =
                        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Stores_Discipline_Code\" " +
                        "ON \"Stores\" (\"Discipline\", \"Code\")";
                    create.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    /// <summary>PriceItems.Notes kolonu — içeri aktarma notları (örn. "TAHMİNİ FİYAT").</summary>
    private static void MigratePriceItemNotesSchema(AppDbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(PriceItems)";
                var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                        existingCols.Add(reader.GetString(1));

                if (!existingCols.Contains("Notes"))
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = "ALTER TABLE PriceItems ADD COLUMN \"Notes\" TEXT";
                    alter.ExecuteNonQuery();
                }
            }
            finally { conn.Close(); }
        }
        catch { /* Migration hatası uygulamayı durdurmasın */ }
    }

    // ─── Veri klasörü eksik uyarısı ───────────────────────────────────────

    private static bool ShowDataRootMissingDialog(AppPathService appPathService)
    {
        var missingPath = appPathService.DataRootPath;
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "HAKEDİŞ DATABASE");

        while (true)
        {
            var result = MessageBox.Show(
                $"HAKEDİŞ DATABASE klasörü bulunamadı.\n\n" +
                $"Kaydedilen konum:\n{missingPath}\n\n" +
                "Klasör silinmiş, taşınmış veya erişilemiyor olabilir.\n\n" +
                "①  Evet  — Klasörü Yeniden Bağla (yeni konum seç)\n" +
                "②  Hayır — Varsayılan Masaüstü Klasörünü Oluştur\n" +
                "③  İptal  — Uygulamayı Kapat",
                "HAKEDİŞ DATABASE Bulunamadı",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
                return false;

            if (result == MessageBoxResult.No)
            {
                // Varsayılan masaüstü klasörünü oluştur
                try
                {
                    Directory.CreateDirectory(defaultPath);
                    appPathService.ChangeDataRootAsync(defaultPath, moveExistingData: false).GetAwaiter().GetResult();
                    return appPathService.DataRootValid;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Klasör oluşturulamadı:\n{ex.Message}",
                        "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                continue;
            }

            // Evet: klasör seç (OpenFolderDialog)
            var dlg = new OpenFolderDialog
            {
                Title = "HAKEDİŞ DATABASE klasörünü seçin",
                Multiselect = false,
            };
            if (dlg.ShowDialog() != true) continue;

            var selected = dlg.FolderName;

            // Seçilen klasörün yapısını doğrula
            var hasDb = File.Exists(Path.Combine(selected, "Data", "hakedis.db"));
            if (!hasDb)
            {
                var confirm = MessageBox.Show(
                    $"Seçilen klasörde veritabanı (Data\\hakedis.db) bulunamadı.\n\n" +
                    "Bu klasöre yine de bağlanmak istiyor musunuz?\n" +
                    "(Yeni veritabanı oluşturulacak)",
                    "Veritabanı Bulunamadı",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) continue;
            }

            appPathService.ChangeDataRootAsync(selected, moveExistingData: false).GetAwaiter().GetResult();
            if (appPathService.DataRootValid) return true;
        }
    }

    private static void CreateDesktopShortcutIfNeeded()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var shortcutPath = Path.Combine(desktop, "Servis Hakediş.lnk");

            // Kısayol varsa hedef ve ikon konumu doğru mu kontrol et; farklıysa güncelle
            if (File.Exists(shortcutPath))
            {
                var shellCheck = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                var existingLink = shellCheck.GetType().InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shellCheck,
                    new object[] { shortcutPath })!;
                var existingTarget = (string?)existingLink.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, existingLink, null);
                var existingIcon = (string?)existingLink.GetType().InvokeMember("IconLocation",
                    System.Reflection.BindingFlags.GetProperty, null, existingLink, null);
                var expectedIcon = exePath + ",0";
                if (string.Equals(existingTarget, exePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existingIcon, expectedIcon, StringComparison.OrdinalIgnoreCase))
                    return; // Zaten doğru yeri ve ikonu gösteriyor
            }

            // WScript.Shell COM nesnesi ile kısayol oluştur / güncelle
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.GetType().InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath })!;
            var linkType = link.GetType();
            linkType.InvokeMember("TargetPath",
                System.Reflection.BindingFlags.SetProperty, null, link, new object[] { exePath });
            linkType.InvokeMember("WorkingDirectory",
                System.Reflection.BindingFlags.SetProperty, null, link,
                new object[] { Path.GetDirectoryName(exePath)! });
            linkType.InvokeMember("Description",
                System.Reflection.BindingFlags.SetProperty, null, link,
                new object[] { "Servis Hakediş Otomasyonu" });
            linkType.InvokeMember("IconLocation",
                System.Reflection.BindingFlags.SetProperty, null, link,
                new object[] { exePath + ",0" });
            linkType.InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, link, null);
        }
        catch { /* Kısayol oluşturulamazsa sessizce geç */ }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("UI Thread Exception", e.Exception);
        MessageBox.Show("Beklenmeyen bir hata oluştu. Detaylar hata kayıt dosyasına yazıldı.",
            "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogError("AppDomain UnhandledException", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError("Unobserved Task Exception", e.Exception);
        e.SetObserved();
    }

    public static void LogError(string context, Exception ex)
    {
        try
        {
            // Önce AppData config'deki klasörü dene; yoksa AppData fallback kullan
            var config = AppDataSettings.Load();
            string logDir;
            if (!string.IsNullOrWhiteSpace(config.DataRootPath) && Directory.Exists(config.DataRootPath))
                logDir = Path.Combine(config.DataRootPath, "Logs");
            else
                logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HakedisOtomasyon", "Logs");

            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "error-log.txt");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n{ex}\n\n";
            File.AppendAllText(logFile, entry);
        }
        catch { /* log yazılamazsa sessiz geç */ }
    }
}
