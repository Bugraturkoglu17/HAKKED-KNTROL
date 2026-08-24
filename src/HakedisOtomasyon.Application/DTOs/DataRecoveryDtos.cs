namespace HakedisOtomasyon.Application.DTOs;

/// <summary>
/// Kurtarılabilir eski veritabanı veya kaynak hakkında bilgi.
/// </summary>
public class OldDatabaseInfo
{
    public string Path         { get; set; } = "";
    public string Label        { get; set; } = ""; // "Masaüstü / HAKEDİŞ DATABASE" gibi
    public int    StoreCount   { get; set; }
    public int    PriceItemCount { get; set; }
    public DateTime LastModified { get; set; }
}

/// <summary>
/// Tüm kurtarma kaynaklarının tarama sonucu.
/// </summary>
public class DataRecoveryScanResult
{
    public bool     HasProtectedData      { get; set; }
    public int      ProtectedStoreCount   { get; set; }
    public int      ProtectedPriceItemCount { get; set; }
    public int      ProtectedAliasCount   { get; set; }
    public DateTime? ProtectedDataDate    { get; set; }

    public List<OldDatabaseInfo> FoundDatabases { get; set; } = [];

    public bool HasAnySource => HasProtectedData || FoundDatabases.Count > 0;
}

/// <summary>
/// Kurtarma işlemi sonucu.
/// </summary>
public class DataRecoveryResult
{
    public int  StoresAdded      { get; set; }
    public int  StoresUpdated    { get; set; }
    public int  PriceItemsAdded  { get; set; }
    public int  PriceItemsUpdated { get; set; }
    public int  AliasesAdded     { get; set; }
    public int  AliasesUpdated   { get; set; }
    public List<string> Errors   { get; set; } = [];

    /// Doğrudan DB kopyalama işlemi için özel mesaj (merge değil).
    public string? CustomMessage { get; set; }
    /// true ise sonuç ekranında yeniden başlat uyarısı gösterilir.
    public bool NeedsRestart     { get; set; }

    public int TotalRestored => StoresAdded + StoresUpdated + PriceItemsAdded + PriceItemsUpdated + AliasesAdded;

    public string Summary =>
        CustomMessage ??
        ($"{StoresAdded + StoresUpdated} mağaza, " +
         $"{PriceItemsAdded + PriceItemsUpdated} fiyat kalemi" +
         (AliasesAdded + AliasesUpdated > 0 ? $", {AliasesAdded + AliasesUpdated} alias" : "") +
         " geri yüklendi.");
}
