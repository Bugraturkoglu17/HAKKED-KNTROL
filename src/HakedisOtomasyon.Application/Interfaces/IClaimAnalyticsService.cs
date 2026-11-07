using HakedisOtomasyon.Application.Analytics;

namespace HakedisOtomasyon.Application.Interfaces;

/// <summary>Aktif disiplinin hakedişlerinden maliyet/trend ve operasyon göstergelerini üretir.</summary>
public interface IClaimAnalyticsService
{
    Task<ClaimAnalyticsResult> GetAsync(AnalyticsFilter filter);

    /// <summary>Kaydı olan en son dönem (yoksa null). Analiz ekranı bu dönemle açılır.</summary>
    Task<Period?> GetLatestPeriodAsync();

    /// <summary>Kaydı olan en eski dönem (yoksa null). "Tüm zamanlar" aralığının başlangıcı.</summary>
    Task<Period?> GetEarliestPeriodAsync();
}
