using System.Text.RegularExpressions;

namespace HakedisOtomasyon.Application;

/// <summary>Servis formundan okunan form numarası (yapay zekâ kullanılmaz; Windows'un yerleşik OCR'ı).</summary>
/// <param name="Number">Okunan numara; bulunamadıysa null.</param>
/// <param name="PageCount">Dosyanın sayfa sayısı (görsel dosyada 1).</param>
/// <param name="Message">Kullanıcıya gösterilecek açıklama (bulunamadı, çok sayfalı ...).</param>
public sealed record FormNumberReadResult(string? Number, int PageCount, string? Message);

/// <summary>
/// Servis formunun sağ üst köşesindeki form numarasını okur (örn. INTİKOŞ formlarında kırmızı "20750").
/// Yalnız TEK sayfalı dosyalarda çalışır — çok sayfalı PDF'de hangi formun okunacağı belli olmadığından okuma yapılmaz.
/// </summary>
public interface IFormNumberReader
{
    /// <summary>Dosyanın sayfa sayısı (PDF) ya da 1 (görsel). Okunamazsa 0.</summary>
    Task<int> GetPageCountAsync(string absoluteFilePath);

    Task<FormNumberReadResult> ReadAsync(string absoluteFilePath);
}

/// <summary>OCR'dan gelen satırlar içinden form numarasını seçen saf kurallar (test edilebilir).</summary>
public static class FormNumberParser
{
    // Adres, telefon, e-posta, web satırları elenir (form başlığında aynı bölgede bulunurlar)
    private static readonly Regex HeaderNoise = new("(?i)tel|fax|faks|sok|no:|@|www|\\.com|sanay|ankara|mah", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"^[1-9]\d{3,6}$", RegexOptions.Compiled);

    /// <summary>
    /// Yalnızca 4–7 haneli bir sayıdan oluşan satırı form numarası sayar. OCR'ın rakam sanılabilecek harf karışıklıkları
    /// (O→0) düzeltilir, satır içi boşluklar atılır; sıfırla başlayan sayılar (sayfa/kod parçaları) alınmaz. Birden çok aday
    /// varsa en uzunu (eşitse ilki) seçilir.
    /// </summary>
    public static string? Pick(IEnumerable<string> ocrLines)
    {
        string? best = null;
        foreach (var raw in ocrLines)
        {
            var line = (raw ?? string.Empty).Trim();
            if (line.Length == 0 || HeaderNoise.IsMatch(line)) continue;
            var compact = line.Replace(" ", string.Empty).Replace('O', '0').Replace('o', '0');
            if (!Number.IsMatch(compact)) continue;
            if (best is null || compact.Length > best.Length) best = compact;
        }
        return best;
    }
}
