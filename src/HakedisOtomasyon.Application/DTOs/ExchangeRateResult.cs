namespace HakedisOtomasyon.Application.DTOs;

/// <summary>
/// TCMB'den alınan döviz kuru sonucu.
/// </summary>
public class ExchangeRateResult
{
    public DateTime RequestedDate { get; set; }
    /// <summary>
    /// Kurun gerçekte yayımlandığı tarih (hafta sonu / tatil geri çekilmesi).
    /// </summary>
    public DateTime ActualDate { get; set; }
    public decimal Rate { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public string Source { get; set; } = "TCMB";
    /// <summary>
    /// true → seçilen tarihte kur bulunamadı, bir önceki iş günü kuru kullanıldı.
    /// </summary>
    public bool IsFallbackDate { get; set; }
    public string? Message { get; set; }
    public bool IsSuccess => Rate > 0;
}
