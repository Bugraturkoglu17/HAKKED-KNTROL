namespace HakedisOtomasyon.Application.Interfaces;

public interface IFileStorageService
{
    Task<string> SaveServiceFormAsync(Stream stream, string fileName, int year, int month);
    Task<string> SaveInvoiceAsync(Stream stream, string fileName, int year, int month);
    Task<string> SaveExportAsync(Stream stream, string fileName, int year, int month);

    /// <summary>Disk üzerindeki bir dosyayı ServisFormlari klasörüne kopyalar. Tam dosya yolunu döner.</summary>
    Task<string> CopyServiceFormAsync(string sourcePath, int year, int month);

    /// <summary>Disk üzerindeki bir dosyayı Faturalar klasörüne kopyalar. Tam dosya yolunu döner.</summary>
    Task<string> CopyInvoiceAsync(string sourcePath, int year, int month);

    /// <summary>Saklanan dosya yolunu mutlak yola çevirir. Zaten mutlaksa olduğu gibi döner.</summary>
    string GetAbsolutePath(string storedPath);
    string GetFileUrl(string storedPath);
    bool FileExists(string storedPath);
    void DeleteFile(string storedPath);
}
