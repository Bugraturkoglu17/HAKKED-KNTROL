namespace HakedisOtomasyon.Domain.Enums;

/// <summary>
/// Soğutma hakedişi OLUŞTURULURKEN seçilen hakediş türü. Her türün hesaplama/kalem mantığı farklıdır;
/// bu yüzden hakediş oluşturmanın başında seçilir ve hakediş kaydında saklanır.
/// Değerler, Hakediş Kontrolü modülündeki HakedisCategory ile AYNI sırada ve numaradadır
/// (ileride iki taraf birbirine bağlanabilsin diye).
/// </summary>
public enum CoolingClaimCategory
{
    CompressorReplacement = 0,
    GlycolUsage = 1,
    EvapReplacement = 2,
    PartialRenovation = 3,
    GasUsage = 4,
    Monitoring = 5,
    PeriodicMaintenance = 6,
    /// <summary>Servis ve bakım malzemeleri hakedişi. (Hakediş Kontrolü tarafında aynı numara "İlave İşler"dir;
    /// hakediş OLUŞTURMA tarafında o başlık yerine bu tür kullanılır.)</summary>
    MaintenanceRepair = 7,
}

public static class CoolingClaimCategoryExtensions
{
    public static string DisplayName(this CoolingClaimCategory category) => category switch
    {
        CoolingClaimCategory.CompressorReplacement => "Kompresör Değişim",
        CoolingClaimCategory.GlycolUsage => "Glikol Kullanım",
        CoolingClaimCategory.EvapReplacement => "Evap Temin ve Değişim",
        CoolingClaimCategory.PartialRenovation => "Kısmi Tadilat",
        CoolingClaimCategory.GasUsage => "Gaz Kullanım",
        CoolingClaimCategory.Monitoring => "İzleme Bedelleri",
        CoolingClaimCategory.PeriodicMaintenance => "Periyodik Bakım",
        CoolingClaimCategory.MaintenanceRepair => "Bakım & Onarım",
        _ => category.ToString(),
    };

    /// <summary>
    /// Bu tür için hakediş oluşturma altyapısı hazır mı? Yeni bir türün alt yapısı yazıldığında
    /// yalnızca burası güncellenir; hazır olmayan türler seçim listesinde "yakında" olarak pasif görünür.
    /// </summary>
    public static bool IsAvailable(this CoolingClaimCategory category) => category switch
    {
        CoolingClaimCategory.GlycolUsage => true,
        CoolingClaimCategory.CompressorReplacement => true,
        CoolingClaimCategory.PartialRenovation => true,
        CoolingClaimCategory.MaintenanceRepair => true,
        _ => false,
    };
}
