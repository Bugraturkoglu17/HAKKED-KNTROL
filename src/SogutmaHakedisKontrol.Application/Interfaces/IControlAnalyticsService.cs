using SogutmaHakedisKontrol.Application.Analytics;

namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>Hakediş kontrol sonuçlarından (fiyat + AI form kontrolü) analiz verisi üretir.</summary>
public interface IControlAnalyticsService
{
    Task<ControlAnalyticsResult> GetAsync(ControlAnalyticsFilter filter);

    /// <summary>Kaydı olan en son kontrol dönemi (yıl, ay); kayıt yoksa null.</summary>
    Task<(int Year, int Month)?> GetLatestPeriodAsync();
}
