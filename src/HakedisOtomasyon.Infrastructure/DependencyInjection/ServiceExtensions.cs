using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Infrastructure.Data;
using HakedisOtomasyon.Infrastructure.ExcelExport;
using HakedisOtomasyon.Infrastructure.FileStorage;
using HakedisOtomasyon.Infrastructure.Ocr;
using HakedisOtomasyon.Infrastructure.PdfProcessing;
using HakedisOtomasyon.Infrastructure.Services;
using HakedisOtomasyon.Infrastructure.Services.Calculation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HakedisOtomasyon.Infrastructure.DependencyInjection;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration,
        AppPathService? appPathService = null)
    {
        // Dosya yolu servisi -- Masaustu\HAKEDIS DATABASE veya kullanicinin sectigi konum
        // Disaridan verilmezse yeni instance olusturulur (AppData JSON''dan yolunu okur).
        var pathService = appPathService ?? new AppPathService();
        services.AddSingleton<IAppPathService>(pathService);
        services.AddSingleton<IFileStorageService, FileStorageService>();

        // Veritabani -- {DataRootPath}\Data\hakedis.db
        var connectionString = $"Data Source={pathService.DatabasePath}";
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connectionString));

        // Masaustu dosya acma -- Process.Start ile Windows shell uzerinden
        services.AddSingleton<IDesktopFileService, DesktopFileService>();

        // Uygulama servisleri
        services.AddScoped<IStoreService, StoreService>();
        services.AddScoped<IPriceListService, PriceListService>();
        services.AddScoped<IProgressClaimService, ProgressClaimService>();
        services.AddScoped<IServiceFormService, ServiceFormService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IExcelExportService, ExcelExportService>();
        services.AddScoped<IDataBackupService, DataBackupService>();
        services.AddScoped<IFileCleanupService, FileCleanupService>();
        services.AddScoped<IDataRecoveryService, DataRecoveryService>();
        services.AddSingleton<RecoveryStateService>();

        // PDF ve dosya isleme
        services.AddScoped<IPdfPreviewService, PdfPreviewService>();

        // OCR -- devre disi (ilk surum)
        services.AddSingleton<IOcrService, DisabledOcrService>();

        // POS eslestirme -- singleton (Excel dosyasi bir kere yuklenir)
        services.AddSingleton<IPosMappingService, PosMappingService>();

        // Disiplin profilleri (Yangin/Klima/Asansor/Sogutma) -- sabit liste, singleton
        services.AddSingleton<IDisciplineProfileService, DisciplineProfileService>();

        // Aktif disiplin durumu -- singleton, varsayilan Fire
        services.AddSingleton<CurrentDisciplineService>();

        // Disiplin bazli hesaplama kurallari -- her disiplin kendi sinifinda izole
        services.AddScoped<FireCalculationRules>();
        services.AddScoped<HvacCalculationRules>();
        services.AddScoped<ElevatorCalculationRules>();
        services.AddScoped<CoolingCalculationRules>();
        services.AddScoped<IDisciplineCalculationRuleFactory, DisciplineCalculationRuleFactory>();

        // TCMB doviz kuru servisi (HttpClient + DB onbellekli)
        services.AddHttpClient<IExchangeRateService, TcmbExchangeRateService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HakedisOtomasyon/1.0");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        return services;
    }
}