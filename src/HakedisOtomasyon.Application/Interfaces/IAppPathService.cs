namespace HakedisOtomasyon.Application.Interfaces;

/// <summary>
/// Uygulamanın merkezi klasör yollarını sağlar.
/// Ana veri klasörü: Masaüstü\HAKEDİŞ DATABASE (veya kullanıcının seçtiği konum).
/// </summary>
public interface IAppPathService
{
    /// <summary>Ana veri klasörü. Örn: C:\Users\...\Desktop\HAKEDİŞ DATABASE</summary>
    string DataRootPath { get; }

    /// <summary>Geriye dönük uyumluluk için DataRootPath takma adı.</summary>
    string AppRoot { get; }

    /// <summary>Veritabanı dosyası. Örn: {DataRootPath}\Data\hakedis.db</summary>
    string DatabasePath { get; }

    /// <summary>{DataRootPath}\Data</summary>
    string DataPath { get; }

    /// <summary>{DataRootPath}\Hakedişler</summary>
    string HakedislerPath { get; }

    /// <summary>{DataRootPath}\MasterData — Kalıcı ana veri klasörü (asla silinmez)</summary>
    string MasterDataPath { get; }

    /// <summary>{DataRootPath}\MasterData\StoreImports</summary>
    string StoreImportsPath { get; }

    /// <summary>StoreImports için geriye dönük uyumluluk takma adı.</summary>
    string StoresCatalogPath { get; }

    /// <summary>{DataRootPath}\MasterData\PriceCatalogImports</summary>
    string PriceCatalogImportsPath { get; }

    /// <summary>PriceCatalogImports için geriye dönük uyumluluk takma adı.</summary>
    string PriceCatalogPath { get; }

    /// <summary>{DataRootPath}\Backups</summary>
    string BackupsPath { get; }

    /// <summary>{DataRootPath}\Logs</summary>
    string LogsPath { get; }

    /// <summary>{DataRootPath}\Temp</summary>
    string TempPath { get; }

    /// <summary>Veri klasörünün geçerli ve erişilebilir olup olmadığı.</summary>
    bool DataRootValid { get; }

    // ─── Ay/Klasör yolları ──────────────────────────────────────────────

    /// <summary>Ay klasörü. Örn: {DataRootPath}\Hakedişler\2026\05 - Mayıs</summary>
    string GetMonthFolder(int year, int month);

    /// <summary>Servis formları klasörü. Örn: ...\05 - Mayıs\Servis Formları</summary>
    string GetServiceFormsFolder(int year, int month);

    /// <summary>Faturalar klasörü. Örn: ...\05 - Mayıs\Faturalar</summary>
    string GetInvoicesFolder(int year, int month);

    /// <summary>Excel çıktıları klasörü. Örn: ...\05 - Mayıs\Excel Çıktıları</summary>
    string GetExcelExportsFolder(int year, int month);

    /// <summary>Arşiv ana klasörü. Örn: ...\05 - Mayıs\Arşiv</summary>
    string GetArchiveFolder(int year, int month);

    /// <summary>Arşivlenmiş servis formları klasörü.</summary>
    string GetArchiveServiceFormsFolder(int year, int month);

    /// <summary>Arşivlenmiş faturalar klasörü.</summary>
    string GetArchiveInvoicesFolder(int year, int month);

    /// <summary>Arşivlenmiş Excel çıktıları klasörü.</summary>
    string GetArchiveExcelExportsFolder(int year, int month);

    // Geriye dönük uyumluluk takma adları
    string GetServiceFormsPath(int year, int month);
    string GetInvoicesPath(int year, int month);
    string GetExportsPath(int year, int month);

    // ─── Path dönüşüm yardımcıları ──────────────────────────────────────

    /// <summary>Göreceli yolu mutlak yola çevirir (DataRootPath + relativePath).</summary>
    string ToAbsolutePath(string relativePath);

    /// <summary>Mutlak yolu DataRootPath'e göre göreceli yola çevirir.</summary>
    string ToRelativePath(string absolutePath);

    // ─── Veri klasörü yönetimi ──────────────────────────────────────────

    /// <summary>Veri klasörü yapısını doğrular. True = geçerli ve erişilebilir.</summary>
    bool ValidateDataRoot();

    /// <summary>
    /// Veri klasörünü yeni konuma günceller. Ayarları AppData JSON'a kaydeder.
    /// moveExistingData = true ise mevcut dosyaları yeni konuma kopyalar.
    /// Değişiklikler uygulamayı yeniden başlatınca tam olarak devreye girer.
    /// </summary>
    Task ChangeDataRootAsync(string newPath, bool moveExistingData);
}
