namespace HakedisOtomasyon.Application.DTOs;

// ─── Zip içindeki JSON modellerine karşılık gelen serialization DTO'ları ───

public class MasterDataMetadataDto
{
    public DateTime ExportDate { get; set; }
    public string AppVersion { get; set; } = "1.0";
    public int StoreCount { get; set; }
    public int PriceItemCount { get; set; }
    public int AliasCount { get; set; }
    public int SettingCount { get; set; }
}

public class StoreBackupDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class PriceItemBackupDto
{
    public string? SourceSheetName { get; set; }
    public int? SourceRowNumber { get; set; }
    public string? PozNo { get; set; }
    public string? MainCategory { get; set; }
    public string? SubCategory { get; set; }
    public string? SubCategory2 { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? InvoiceDescription { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal MaterialPrice { get; set; }
    public decimal LaborPrice { get; set; }
    public int PriceType { get; set; }
    public string? SearchText { get; set; }
    public bool IsSelectable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsManuallyAdded { get; set; }
}

public class PriceAliasBackupDto
{
    public string Keyword { get; set; } = string.Empty;
    public string NormalizedKeyword { get; set; } = string.Empty;
    public string TargetText { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class AppSettingBackupDto
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
}

// ─── Preview / sonuç DTO'ları ───

public enum ImportMode
{
    MergeAndUpdate,   // Varsayılan: eşleşenleri güncelle, eksikleri ekle
    AddMissingOnly,   // Sadece yeni kayıtları ekle, mevcutlara dokunma
    ReplaceAll        // Tüm mevcut ana veriyi sil, yedekten baştan yükle
}

public class MasterDataPreviewDto
{
    // ─── Gösterim sayaçları ───
    public int StoresToAdd { get; set; }
    public int StoresToUpdate { get; set; }
    public int StoresToSkip { get; set; }

    public int PriceItemsToAdd { get; set; }
    public int PriceItemsToUpdate { get; set; }
    public int PriceItemsToSkip { get; set; }

    public int AliasesToAdd { get; set; }
    public int AliasesToUpdate { get; set; }
    public int AliasesToSkip { get; set; }

    public int SettingsToUpdate { get; set; }

    public MasterDataMetadataDto? Metadata { get; set; }

    // ─── İçe aktarılacak ham veri (ApplyImportAsync tarafından kullanılır) ───
    public List<StoreBackupDto> StoreData { get; set; } = new();
    public List<PriceItemBackupDto> PriceItemData { get; set; } = new();
    public List<PriceAliasBackupDto> AliasData { get; set; } = new();
    public List<AppSettingBackupDto> SettingData { get; set; } = new();

    public bool HasChanges =>
        StoresToAdd + StoresToUpdate +
        PriceItemsToAdd + PriceItemsToUpdate +
        AliasesToAdd + AliasesToUpdate +
        SettingsToUpdate > 0;
}

public class MasterDataImportResultDto
{
    public int StoresAdded { get; set; }
    public int StoresUpdated { get; set; }
    public int StoresSkipped { get; set; }
    public int PriceItemsAdded { get; set; }
    public int PriceItemsUpdated { get; set; }
    public int PriceItemsSkipped { get; set; }
    public int AliasesAdded { get; set; }
    public int SettingsUpdated { get; set; }

    public string Summary =>
        $"{StoresAdded + StoresUpdated} mağaza, " +
        $"{PriceItemsAdded + PriceItemsUpdated} fiyat kalemi" +
        (AliasesAdded > 0 ? $", {AliasesAdded} alias" : "") +
        " içeri aktarıldı.";
}
