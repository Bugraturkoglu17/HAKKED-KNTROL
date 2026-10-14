namespace HakedisOtomasyon.Application;

/// <summary>
/// Servis formu ekranlarında sol paneldeki ileri/geri okların konumu: dosya + (çok sayfalı PDF'de) sayfa.
/// Çok sayfalı PDF'de her sayfa ayrı servis formudur; oklar önce dosyanın sayfalarında, sonra sonraki/önceki dosyada gezer.
/// Sayfa sayısı bilinmeyen dosya tek sayfa sayılır (sayılar <see cref="SetPageCount"/> ile sonradan gelir).
/// </summary>
public sealed class FilePageCursor
{
    private readonly Dictionary<string, int> _pageCounts = new(StringComparer.Ordinal);

    /// <summary>Açık dosyanın sırası (0 tabanlı).</summary>
    public int FileIndex { get; private set; }

    /// <summary>Açık dosyadaki sayfa (0 tabanlı).</summary>
    public int PageIndex { get; private set; }

    public bool HasPageCount(string filePath) => _pageCounts.ContainsKey(filePath);

    public void SetPageCount(string filePath, int count) => _pageCounts[filePath] = Math.Max(1, count);

    /// <summary>Dosyanın sayfa sayısı (bilinmiyorsa 1).</summary>
    public int PagesOf(string? filePath) => filePath is not null && _pageCounts.TryGetValue(filePath, out var n) ? n : 1;

    /// <summary>Dosyayı ilk sayfasından açar.</summary>
    public void GoToFile(int fileIndex)
    {
        FileIndex = Math.Max(0, fileIndex);
        PageIndex = 0;
    }

    /// <summary>Dosya listesi değişince (silme vb.) konumu geçerli aralığa çeker.</summary>
    public void Clamp(IReadOnlyList<string> files)
    {
        if (FileIndex >= files.Count) { FileIndex = Math.Max(0, files.Count - 1); PageIndex = 0; }
        if (files.Count > 0 && PageIndex >= PagesOf(files[FileIndex])) PageIndex = 0;
    }

    public bool CanPrevious => FileIndex > 0 || PageIndex > 0;

    public bool CanNext(IReadOnlyList<string> files)
        => files.Count > 0 && (PageIndex < PagesOf(files[Math.Min(FileIndex, files.Count - 1)]) - 1 || FileIndex < files.Count - 1);

    /// <summary>Bir önceki form: aynı dosyanın önceki sayfası, yoksa önceki dosyanın SON sayfası.</summary>
    public bool Previous(IReadOnlyList<string> files)
    {
        if (PageIndex > 0) { PageIndex--; return true; }
        if (FileIndex <= 0) return false;
        FileIndex--;
        PageIndex = PagesOf(files[FileIndex]) - 1;
        return true;
    }

    /// <summary>Bir sonraki form: aynı dosyanın sonraki sayfası, yoksa sonraki dosyanın İLK sayfası.</summary>
    public bool Next(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return false;
        if (PageIndex < PagesOf(files[FileIndex]) - 1) { PageIndex++; return true; }
        if (FileIndex >= files.Count - 1) return false;
        FileIndex++;
        PageIndex = 0;
        return true;
    }
}
