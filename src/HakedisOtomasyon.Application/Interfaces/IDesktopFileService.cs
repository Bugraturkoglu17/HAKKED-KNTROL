namespace HakedisOtomasyon.Application.Interfaces;

/// <summary>
/// Masaüstü uygulaması için yerel dosya açma/gösterme işlemleri.
/// BlazorWebView içinden Process.Start ile Windows shell üzerinden çalışır.
/// </summary>
public interface IDesktopFileService
{
    /// <summary>Dosyayı varsayılan programda açar (Excel, PDF, vb.).</summary>
    void OpenFile(string filePath);

    /// <summary>Klasörü Windows Explorer'da açar.</summary>
    void OpenFolder(string folderPath);

    /// <summary>Dosyayı Windows Explorer'da seçili olarak gösterir.</summary>
    void ShowFileInExplorer(string filePath);
}
