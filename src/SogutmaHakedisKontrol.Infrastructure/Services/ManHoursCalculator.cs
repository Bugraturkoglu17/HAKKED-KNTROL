using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Adam-saat matematiği — AI tarafından değil, burada deterministic olarak yapılır.
/// Kural: ödenebilir_adam_saat = max(0, toplam_adam_saat - 4).
/// </summary>
public class ManHoursCalculator : IManHoursCalculator
{
    private const decimal DeductibleHours = 4m;

    public decimal? CalculateHours(TimeSpan? start, TimeSpan? end)
    {
        if (start is null || end is null) return null;
        var diff = end.Value - start.Value;
        if (diff < TimeSpan.Zero) diff += TimeSpan.FromHours(24); // gece yarısını geçen vardiya
        return Math.Round((decimal)diff.TotalHours, 2, MidpointRounding.AwayFromZero);
    }

    public decimal CalculatePayableHours(decimal totalManHours) => Math.Max(0, totalManHours - DeductibleHours);
}
