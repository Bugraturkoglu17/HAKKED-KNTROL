namespace HakedisOtomasyon.Application.DTOs;

/// <summary>
/// Tam yedek başarıyla oluşturuldu, ancak bazı dosyalar (açık Excel/görsel vb.)
/// kilitli olduğu için ZIP'e eklenemedi.
/// </summary>
public sealed class PartialBackupException : Exception
{
    /// <summary>Oluşturulan ZIP dosyasının tam yolu.</summary>
    public string BackupPath { get; }

    /// <summary>Kilitli olduğu için atlatılan dosyaların yolları.</summary>
    public IReadOnlyList<string> SkippedFiles { get; }

    public PartialBackupException(string backupPath, IReadOnlyList<string> skippedFiles)
        : base("Yedek alındı ancak açık olan bazı dosyalar kopyalanamadı.")
    {
        BackupPath   = backupPath;
        SkippedFiles = skippedFiles;
    }
}
