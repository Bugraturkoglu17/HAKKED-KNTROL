namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>Masaüstü uygulaması için yerel dosya açma işlemleri (Process.Start ile Windows shell).</summary>
public interface IDesktopFileService
{
    void OpenFile(string filePath);
}
