using System.Globalization;

namespace HakedisOtomasyon.Application;

/// <summary>
/// TL tutarının Türkçe biçimde yazılıp okunması: "29.682,53" (binlik ayırıcı nokta, ondalık virgül).
/// Makinenin bölge ayarından BAĞIMSIZDIR — bazı makinelerde virgül binlik ayırıcı sayıldığından sayı alanları "29682,53"ü
/// yanlış okuyabiliyor; bu yüzden tutar metin olarak alınıp burada çözülür.
/// </summary>
public static class TrMoney
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>"29.682,53" — her zaman iki ondalık, binlik ayırıcılı.</summary>
    public static string Format(decimal amount) => amount.ToString("N2", Tr);

    /// <summary>
    /// Kullanıcının yazdığı tutarı çözer. Kabul edilenler: "29682,53", "29.682,53", "29682.53", "29.682", "29 682,53 ₺", "1.234.567".
    /// Kural: virgül varsa ondalık ayırıcı virgüldür (noktalar binliktir). Virgül yoksa: tek nokta ve ardından TAM 3 rakam varsa
    /// ("29.682") ya da birden çok nokta varsa nokta binliktir; aksi halde ("29682.53", sayısal tuş takımı) nokta ondalıktır.
    /// </summary>
    public static bool TryParse(string? text, out decimal amount)
    {
        amount = 0m;
        var s = new string((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != ' ').ToArray())
            .Replace("₺", string.Empty).Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (s.Length == 0 || s.Any(c => !char.IsDigit(c) && c != '.' && c != ',')) return false;

        string normalized;
        int commas = s.Count(c => c == ',');
        int dots = s.Count(c => c == '.');
        if (commas > 1) return false;
        if (commas == 1)
        {
            if (s.IndexOf('.') > s.IndexOf(',')) return false;                  // "1,234.56" biçimi kabul edilmez (karışıklık olmasın)
            normalized = s.Replace(".", string.Empty).Replace(',', '.');
        }
        else if (dots == 0) normalized = s;
        else if (dots == 1)
        {
            var after = s.Length - s.IndexOf('.') - 1;
            normalized = after == 3 && s.IndexOf('.') > 0 ? s.Replace(".", string.Empty) : s;   // "29.682" → 29682 ; "29682.53" → 29682,53
        }
        else normalized = s.Replace(".", string.Empty);                          // "1.234.567"

        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }
}
