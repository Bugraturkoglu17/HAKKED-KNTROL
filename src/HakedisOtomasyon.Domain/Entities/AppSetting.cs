namespace HakedisOtomasyon.Domain.Entities;

public class AppSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public static class SettingKeys
{
    public const string DefaultServiceFee = "DefaultServiceFee";
    public const string InvoiceMarkupRate = "InvoiceMarkupRate";
    public const string CompanyName = "CompanyName";
    public const string CompanyAddress = "CompanyAddress";
    public const string CompanyPhone = "CompanyPhone";
    public const string ExcelOutputPath = "ExcelOutputPath";
    public const string ThemeMode = "ThemeMode";
    // MasterData — kalıcı Excel kaynak dosya yolları
    public const string StoreExcelFilePath          = "StoreExcelFilePath";
    public const string LastStoreImportDate         = "LastStoreImportDate";
    public const string PriceCatalogExcelFilePath   = "PriceCatalogExcelFilePath";
    public const string LastPriceCatalogImportDate  = "LastPriceCatalogImportDate";
    // Arşiv
    public const string ArchiveRetentionDays = "ArchiveRetentionDays";
    // Excel görsel kalitesi: "low" | "standard" | "high"
    public const string ExcelImageQuality = "ExcelImageQuality";
}
