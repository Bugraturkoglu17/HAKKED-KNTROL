using System.Text.RegularExpressions;

namespace HakedisOtomasyon.Application;

/// <summary>Servis formundan okunan form numarası (yapay zekâ kullanılmaz; Windows'un yerleşik OCR'ı).</summary>
/// <param name="Number">Okunan numara; bulunamadıysa null.</param>
/// <param name="PageCount">Dosyanın sayfa sayısı (görsel dosyada 1).</param>
/// <param name="Message">Kullanıcıya gösterilecek açıklama (bulunamadı, sayfa yok ...).</param>
public sealed record FormNumberReadResult(string? Number, int PageCount, string? Message);

/// <summary>
/// Servis formunun sağ üst köşesindeki form numarasını okur (örn. INTİKOŞ formlarında kırmızı "20750").
/// Çok sayfalı PDF'de her sayfa ayrı servis formudur; yalnız istenen sayfa okunur.
/// </summary>
public interface IFormNumberReader
{
    /// <summary>Dosyanın sayfa sayısı (PDF) ya da 1 (görsel). Okunamazsa 0.</summary>
    Task<int> GetPageCountAsync(string absoluteFilePath);

    /// <param name="pageIndex">Okunacak sayfa (0 tabanlı; görselde 0).</param>
    Task<FormNumberReadResult> ReadAsync(string absoluteFilePath, int pageIndex = 0);
}

/// <summary>OCR'dan gelen satırlar içinden form numarasını seçen saf kurallar (test edilebilir).</summary>
public static class FormNumberParser
{
    // Adres, telefon, e-posta, web satırları elenir (form başlığında aynı bölgede bulunurlar)
    private static readonly Regex HeaderNoise = new("(?i)tel|fax|faks|sok|no:|@|www|\\.com|sanay|ankara|mah", RegexOptions.Compiled);
    // Sıfırla başlayan numara yalnız 6–7 haneliyse kabul edilir (OKUR: "005027"); kısa olanlar ("0100") sayfa/kod parçasıdır.
    private static readonly Regex ZeroPadded = new(@"^0\d{5,6}$", RegexOptions.Compiled);

    /// <summary>
    /// Yalnızca bir sayıdan oluşan satırı form numarası sayar: sıfırla başlamayan <paramref name="minDigits"/>–7 hane
    /// (INTİKOŞ: "20750", "8273") ya da sıfırla başlayan 6–7 hane (OKUR: "005027"; baştaki sıfırlar korunur). OCR'ın rakam
    /// sanılabilecek harf karışıklıkları (O→0) düzeltilir, satır içi boşluklar atılır. Birden çok aday varsa en uzunu seçilir.
    /// </summary>
    /// <param name="minDigits">Sıfırla başlamayan numaranın en az hane sayısı. Kırmızı damga okumasında 4; kırmızı olmayan
    /// (düz) okumada 5 kullanılır — kaşedeki vergi no parçası ("9513") ya da elle yazılmış 4 haneli mağaza kodu numara sanılmasın.</param>
    public static string? Pick(IEnumerable<string> ocrLines, int minDigits = 4)
    {
        var plain = new Regex($@"^[1-9]\d{{{minDigits - 1},6}}$");
        string? best = null;
        foreach (var raw in ocrLines)
        {
            var line = (raw ?? string.Empty).Trim();
            if (line.Length == 0 || HeaderNoise.IsMatch(line)) continue;
            var compact = line.Replace(" ", string.Empty).Replace('O', '0').Replace('o', '0');
            if (!plain.IsMatch(compact) && !ZeroPadded.IsMatch(compact)) continue;
            if (best is null || compact.Length > best.Length) best = compact;
        }
        return best;
    }
}
