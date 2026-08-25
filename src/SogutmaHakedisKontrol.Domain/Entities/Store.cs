namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Mağaza ana listesi kaydı. Hakediş kontrolü ve AI belge analizi boyunca
/// mağaza tespitinin tek referans kaynağıdır — AI mağaza adını/kodunu tekrar tahmin etmez,
/// yalnızca aday üretir; kesinleştirme bu tabloya karşı yapılır.
/// </summary>
public class Store
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? StoreRegion { get; set; }   // Excel'deki "bölge" alanı (CompanyName/Region kapsamından farklı olabilir)
    public string? Address { get; set; }

    public string NormalizedCode { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
