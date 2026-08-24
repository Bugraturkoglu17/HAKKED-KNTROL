namespace HakedisOtomasyon.Domain.Entities;

public class PriceAlias
{
    public int Id { get; set; }

    /// <summary>Kullanıcının yazdığı arama kelimesi (orijinal hâli).</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>Arama sırasında kullanılan normalize edilmiş hâl (küçük harf, boşluks.).</summary>
    public string NormalizedKeyword { get; set; } = string.Empty;

    /// <summary>Bu alias'ın işaret ettiği fiyat kalemi açıklaması veya arama metni.</summary>
    public string TargetText { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
