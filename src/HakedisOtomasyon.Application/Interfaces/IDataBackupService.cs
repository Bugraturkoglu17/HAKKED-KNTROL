using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IDataBackupService
{
    /// <summary>
    /// Mağaza, fiyat listesi, alias ve temel ayarları Masaüstü'ne zip olarak dışa aktarır.
    /// Oluşturulan dosyanın tam yolunu döndürür.
    /// </summary>
    Task<string> ExportMasterDataAsync();

    /// <summary>
    /// Seçilen zip dosyasını okuyup içeri aktarma önizlemesini döndürür.
    /// </summary>
    Task<MasterDataPreviewDto> PreviewImportAsync(string zipPath);

    /// <summary>
    /// Önizlemesi alınmış veriyi seçilen mod ile veritabanına uygular.
    /// </summary>
    Task<MasterDataImportResultDto> ApplyImportAsync(MasterDataPreviewDto preview, ImportMode mode);

    /// <summary>
    /// Tüm uygulama veritabanını ve yükleme klasörünü Masaüstü'ne zip olarak yedekler.
    /// Oluşturulan dosyanın tam yolunu döndürür.
    /// </summary>
    Task<string> CreateFullBackupAsync();

    /// <summary>
    /// Seçilen tam yedek zip dosyasını bir sonraki başlatmada geri yüklemek üzere zamanlar.
    /// pending-restore.json marker dosyası oluşturulur; geri yükleme uygulama yeniden
    /// başladığında DbContext açılmadan önce gerçekleştirilir.
    /// </summary>
    void ScheduleFullRestore(string zipPath);

    /// <summary>Veri klasörünü Windows Explorer'da açar.</summary>
    void OpenDataFolder();
}
