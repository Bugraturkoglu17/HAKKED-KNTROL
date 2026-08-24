using System.Diagnostics;
using HakedisOtomasyon.Application.Interfaces;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Windows shell üzerinden dosya/klasör açma işlemleri.
/// Process.Start + UseShellExecute=true — tarayıcı veya web URL'si kullanılmaz.
/// </summary>
public class DesktopFileService : IDesktopFileService
{
    public void OpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Dosya yolu boş olamaz.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Dosya bulunamadı.", filePath);

        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        });
    }

    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("Klasör yolu boş olamaz.", nameof(folderPath));
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Klasör bulunamadı: {folderPath}");

        Process.Start(new ProcessStartInfo
        {
            FileName = folderPath,
            UseShellExecute = true
        });
    }

    public void ShowFileInExplorer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Dosya yolu boş olamaz.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Dosya bulunamadı.", filePath);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = true
        });
    }
}
