namespace SogutmaHakedisKontrol.Domain.Enums;

/// <summary>Hakediş kontrolünün başında kullanıcının seçtiği hakediş türü. AI'nin baktığı alanları
/// ve karşılaştırma mantığını belirler (bkz. ICategoryControlProfile / ICategoryComparisonStrategy).</summary>
public enum HakedisCategory
{
    CompressorReplacement = 0,
    GlycolUsage = 1,
    EvapReplacement = 2,
    PartialRenovation = 3,
    GasUsage = 4,
    Monitoring = 5,
    PeriodicMaintenance = 6,
    AdditionalWork = 7,
}

public static class HakedisCategoryExtensions
{
    public static string DisplayName(this HakedisCategory category) => category switch
    {
        HakedisCategory.CompressorReplacement => "Kompresör Değişim",
        HakedisCategory.GlycolUsage => "Glikol Kullanım",
        HakedisCategory.EvapReplacement => "Evap Temin ve Değişim",
        HakedisCategory.PartialRenovation => "Kısmi Tadilat",
        HakedisCategory.GasUsage => "Gaz Kullanım",
        HakedisCategory.Monitoring => "İzleme Bedelleri",
        HakedisCategory.PeriodicMaintenance => "Periyodik Bakım",
        HakedisCategory.AdditionalWork => "İlave İşler",
        _ => category.ToString(),
    };
}
