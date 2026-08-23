using System.Diagnostics;
using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Infrastructure.FileStorage;

/// <summary>Windows shell üzerinden dosya açma. Process.Start + UseShellExecute=true.</summary>
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
}
