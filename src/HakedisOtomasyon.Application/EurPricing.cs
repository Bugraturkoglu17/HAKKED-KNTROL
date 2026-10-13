using System.Globalization;
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

    /// <summary>Satır toplamı: adet × EUR fiyatı × kur, kuruşa yuvarlanır (birim fiyat önce yuvarlanmaz).</summary>
    public static decimal TotalTl(decimal quantity, decimal eurAmount, decimal rate)
        => Math.Round(quantity * eurAmount * rate, 2, MidpointRounding.AwayFromZero);

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
            item.TotalPrice = TotalTl(item.Quantity, eur, rate);
            count++;
        }
        return count;
    }

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Kuru Türkçe biçimde, gereksiz sıfırsız gösterir: 53,987 (en fazla 4 ondalık).</summary>
    public static string FormatRate(decimal rate) => rate.ToString("0.####", Tr);

    /// <summary>
    /// Kullanıcının yazdığı kuru okur; makine/uygulama kültüründen BAĞIMSIZDIR. Ondalık ayraç olarak virgül ya da
    /// nokta kabul edilir (son ayraç ondalıktır, öncekiler binlik sayılır): "53,987", "53.987", "1.053,5" → geçerli.
    /// Kur 0'dan büyük ve 10.000'den küçük olmalıdır.
    /// </summary>
    public static bool TryParseRate(string? text, out decimal rate)
    {
        rate = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim()
            .Replace("\u00A0", "").Replace(" ", "")
            .Replace("₺", "").Replace("TL", "", StringComparison.OrdinalIgnoreCase);

        int dec = Math.Max(t.LastIndexOf(','), t.LastIndexOf('.'));
        string intPart = dec < 0 ? t : t[..dec];
        string fracPart = dec < 0 ? "" : t[(dec + 1)..];
        intPart = intPart.Replace(",", "").Replace(".", "");

        if (!intPart.All(char.IsAsciiDigit) || !fracPart.All(char.IsAsciiDigit)) return false;
        if (intPart.Length == 0 && fracPart.Length == 0) return false;

        var normalized = (intPart.Length == 0 ? "0" : intPart) + (fracPart.Length > 0 ? "." + fracPart : "");
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out rate) && IsValidRate(rate);
    }

    /// <summary>Kur geçerli mi? (0 veya negatif kurla fiyat hesaplanamaz.)</summary>
    public static bool IsValidRate(decimal? rate) => rate is > 0m and < 10_000m;
}
