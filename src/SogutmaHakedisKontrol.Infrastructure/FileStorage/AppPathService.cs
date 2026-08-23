using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Infrastructure.FileStorage;

/// <summary>Veri klasörü: Masaüstü\SOĞUTMA HAKEDİŞ KONTROL. Sabit — bağımsız/gelişim uygulaması içindir.</summary>
public class AppPathService : IAppPathService
{
    public string DataRootPath { get; }
    public string DatabasePath { get; }

    public AppPathService()
    {
        DataRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "SOĞUTMA HAKEDİŞ KONTROL");
        Directory.CreateDirectory(DataRootPath);

        var dataDir = Path.Combine(DataRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        DatabasePath = Path.Combine(dataDir, "sogutma_hakedis.db");
    }
}
