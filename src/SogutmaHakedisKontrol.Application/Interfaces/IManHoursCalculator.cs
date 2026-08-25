namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IManHoursCalculator
{
    /// <summary>Tek bir personelin çalışma süresi (saat). Gece yarısını geçen vardiyalar desteklenir.</summary>
    decimal? CalculateHours(TimeSpan? start, TimeSpan? end);

    /// <summary>
    /// Toplam adam-saat üzerinden ödenebilir adam-saati hesaplar: max(0, toplam - 4).
    /// Bu matematik AI tarafından değil, burada deterministic olarak yapılır.
    /// </summary>
    decimal CalculatePayableHours(decimal totalManHours);
}
