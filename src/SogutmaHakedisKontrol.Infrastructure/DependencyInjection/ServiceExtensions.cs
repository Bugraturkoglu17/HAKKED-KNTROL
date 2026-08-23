using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.FileStorage;
using SogutmaHakedisKontrol.Infrastructure.Services;

namespace SogutmaHakedisKontrol.Infrastructure.DependencyInjection;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        var appPathService = new AppPathService();
        services.AddSingleton<IAppPathService>(appPathService);

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={appPathService.DatabasePath}"));

        services.AddSingleton<IDesktopFileService, DesktopFileService>();

        services.AddScoped<IMaterialMatchingService, MaterialMatchingService>();
        services.AddScoped<IUnitPriceListService, UnitPriceListService>();
        services.AddScoped<IProgressPaymentCheckService, ProgressPaymentCheckService>();

        return services;
    }
}
