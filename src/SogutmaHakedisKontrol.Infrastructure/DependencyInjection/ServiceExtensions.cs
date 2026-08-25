using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Infrastructure.Configuration;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.FileStorage;
using SogutmaHakedisKontrol.Infrastructure.Services;

namespace SogutmaHakedisKontrol.Infrastructure.DependencyInjection;

public static class ServiceExtensions
{
    public static IServiceCollection AddSogutmaHakedisKontrolServices(this IServiceCollection services)
    {
        // .env dosyası varsa (geliştirici makinesi) — gerçek ortam değişkenlerinin üzerine asla yazmaz.
        DotEnvLoader.Load(AppDomain.CurrentDomain.BaseDirectory);

        var appPathService = new AppPathService();
        services.AddSingleton<IAppPathService>(appPathService);

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={appPathService.DatabasePath}"));

        services.AddSingleton<IDesktopFileService, DesktopFileService>();

        services.AddScoped<IMaterialMatchingService, MaterialMatchingService>();
        services.AddScoped<IUnitPriceListService, UnitPriceListService>();
        services.AddScoped<IProgressPaymentCheckService, ProgressPaymentCheckService>();

        // ── Mağaza ana listesi + AI belge analizi ────────────────────────
        services.AddScoped<IStoreMasterService, StoreMasterService>();
        services.AddScoped<IStoreMatchingService, StoreMatchingService>();
        services.AddScoped<IPdfPageRasterizer, PdfPageRasterizerService>();
        services.AddScoped<IManHoursCalculator, ManHoursCalculator>();
        services.AddScoped<IAiUsageTracker, AiUsageTracker>();
        services.AddScoped<IAiVisionClient, OpenAiVisionClient>();
        services.AddScoped<IAiAnalysisPipelineService, AiAnalysisPipelineService>();

        return services;
    }
}
