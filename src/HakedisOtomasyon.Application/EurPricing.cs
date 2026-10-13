using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application;

/// <summary>
/// Soğutma hakedişinde EUR fiyatlı kalemlerin TL'ye çevrilmesi. Tek yerde tutulur ki kalem eklenirken,
/// hakediş kuru değiştirildiğinde ve testlerde aynı yuvarlama kuralı kullanılsın.
/// </summary>
public static class EurPricing
{
    public const string CurrencyCode = "EUR";

    /// <summary>EUR tutarı × kur, kuruşa (2 hane) yuvarlanır (0,005 yukarı).</summary>
    public static decimal ToTl(decimal eurAmount, decimal rate)
        => Math.Round(eurAmount * rate, 2, MidpointRounding.AwayFromZero);

    public static bool IsEur(string? currencyCode)
        => string.Equals(currencyCode, CurrencyCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Listedeki EUR fiyatlı kalemleri (CurrencyCode=EUR ve ForeignPrice dolu) verilen kurla yeniden fiyatlar;
    /// diğer kalemlere dokunmaz. Etkilenen kalem sayısını döner.
    /// </summary>
    public static int Reprice(IEnumerable<ServiceItemDto> items, decimal rate, DateTime? rateDate, string source)
    {
        int count = 0;
        foreach (var item in items)
        {
            if (!IsEur(item.CurrencyCode) || item.ForeignPrice is not { } eur) continue;
            item.MaterialPrice = ToTl(eur, rate);
            item.LaborPrice = 0m;
            item.ExchangeRate = rate;
            item.ExchangeRateDate = rateDate;
            item.ExchangeRateActualDate = rateDate;
            item.ExchangeRateSource = source;
            item.UnitPrice = item.MaterialPrice;
            item.TotalPrice = Math.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
            count++;
        }
        return count;
    }

    /// <summary>Kur geçerli mi? (0 veya negatif kurla fiyat hesaplanamaz.)</summary>
    public static bool IsValidRate(decimal? rate) => rate is > 0m and < 10_000m;
}
