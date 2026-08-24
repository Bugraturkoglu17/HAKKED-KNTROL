using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IExchangeRateService
{
    /// <summary>
    /// Verilen tarih için USD kurununu döndürür.
    /// Hafta sonu / tatil gibi kurun yayımlanmadığı günlerde en yakın önceki
    /// iş gününün kuruna otomatik geri çekilir (IsFallbackDate=true).
    /// </summary>
    Task<ExchangeRateResult> GetUsdRateAsync(
        DateTime date,
        ExchangeRateType type = ExchangeRateType.ForexSelling);
}
