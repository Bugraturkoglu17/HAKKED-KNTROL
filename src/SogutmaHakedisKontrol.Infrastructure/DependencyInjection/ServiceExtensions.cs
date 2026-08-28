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

        // AI_PROVIDER=ollama (varsayılan) → yerel, internete çıkmayan vision modeli (kurumsal ağ
        // engeli OpenAI'ı bloklarsa bile çalışır). AI_PROVIDER=openai → OpenAI Responses API.
        // AI_PROVIDER=gemini → Google Gemini API (bulut, GEMINI_API_KEY gerekir).
        var aiProvider = (Environment.GetEnvironmentVariable("AI_PROVIDER") ?? "ollama").Trim().ToLowerInvariant();
        if (aiProvider == "openai")
            services.AddScoped<IAiVisionClient, OpenAiVisionClient>();
        else if (aiProvider == "gemini")
            services.AddScoped<IAiVisionClient, GeminiVisionClient>();
        else
            services.AddScoped<IAiVisionClient, OllamaVisionClient>();

        services.AddScoped<IAiAnalysisPipelineService, AiAnalysisPipelineService>();

        // ── Kategori bazlı kontrol profilleri / karşılaştırma stratejileri ──
        services.AddScoped<ICategoryControlProfile, CompressorReplacementProfile>();
        services.AddScoped<ICategoryControlProfile, GlycolUsageProfile>();
        services.AddScoped<ICategoryControlProfile, EvapReplacementProfile>();
        services.AddScoped<ICategoryControlProfile, PartialRenovationProfile>();
        services.AddScoped<ICategoryControlProfile, GasUsageProfile>();
        services.AddScoped<ICategoryControlProfile, MonitoringProfile>();
        services.AddScoped<ICategoryControlProfile, PeriodicMaintenanceProfile>();
        services.AddScoped<ICategoryControlProfile, AdditionalWorkProfile>();
        services.AddScoped<ICategoryControlProfileRegistry, CategoryControlProfileRegistry>();

        services.AddScoped<ICategoryComparisonStrategy, DefaultCategoryComparisonStrategy>();
        services.AddScoped<ICategoryComparisonStrategy, GasUsageComparisonStrategy>();
        services.AddScoped<ICategoryComparisonStrategy, AdditionalWorkComparisonStrategy>();
        services.AddScoped<ICategoryComparisonStrategy, GlycolUsageComparisonStrategy>();
        services.AddScoped<ICategoryComparisonStrategyRegistry, CategoryComparisonStrategyRegistry>();

        return services;
    }
}
