using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.DependencyInjection;
using System.IO;
using System.Windows;

namespace SogutmaHakedisKontrol.Web;

public partial class WpfApp : System.Windows.Application
{
    public IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

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
        });

        services.AddApplicationServices();
        services.AddSingleton<IFilePickerService, WpfFilePickerService>();

        Services = services.BuildServiceProvider();
        Resources["services"] = Services;

        try
        {
            var appPath = Services.GetRequiredService<IAppPathService>();
            SeedDatabaseIfMissing(appPath.DatabasePath);

            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Veritabanı başlatma hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    /// <summary>
    /// İlk çalıştırmada (veya veri klasörü taşındığında) kullanıcının veritabanı henüz yoksa,
    /// uygulamayla birlikte dağıtılan onaylı birim fiyat kataloğunu içeren tohum veritabanını kopyalar.
    /// Böylece proje GitHub'dan başka bir bilgisayara klonlandığında fiyat listesi elle yeniden
    /// içe aktarılmadan hazır gelir. Kullanıcının kendi verisi asla üzerine yazılmaz.
    /// </summary>
    private static void SeedDatabaseIfMissing(string databasePath)
    {
        if (File.Exists(databasePath)) return;

        var seedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SeedData", "sogutma_hakedis_seed.db");
        if (!File.Exists(seedPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.Copy(seedPath, databasePath);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Beklenmeyen hata: {e.Exception.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
