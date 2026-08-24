namespace HakedisOtomasyon.Domain.Entities;

/// <summary>
/// TCMB döviz kuru önbelleği. Her gün / para birimi için tek kayıt tutulur.
/// </summary>
public class ExchangeRateCache
{
    public int Id { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public DateTime RateDate { get; set; }
    public decimal ForexBuying { get; set; }
    public decimal ForexSelling { get; set; }
    public string Source { get; set; } = "TCMB";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
