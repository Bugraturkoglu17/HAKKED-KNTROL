using System.Text.RegularExpressions;

namespace HakedisOtomasyon.Application;

/// <summary>OCR'ın okuduğu bir satır: metin + sayfadaki konumu (sol üst köşeye göre 0–1 arası, satırın ortası).</summary>
public sealed record OcrLine(string Text, double X, double Y);

/// <summary>
/// Azure Document Intelligence'ın servis formundan okuduğu satırlardan mağaza kodu/adı ve servis tarihinin gün/ayını çıkarır
/// (saf kurallar — test edilebilir). Azure etiketi ("ÇAĞRI BİLDİREN :") ile elle yazılan değeri ("5M AFYON") çoğu zaman AYRI
/// satır olarak, aynı hizada döndürür; bu yüzden değer = etiketten sonrası + aynı hizada sağındaki satırlar.
///  • Mağaza: "ÇAĞRI BİLDİREN" (ACİL formunda "ACİL ÇAĞRI BİLDİREN"), yoksa "İŞİN YERİ", OKUR'da "MÜŞTERİ". Parantez içindeki
///    3–5 haneli sayı mağaza kodudur ("GOP MM MİGROS (383)"). Parantezli kod yoksa başlıktan ("... FORMU") önce elle yazılmış
///    tek başına sayı aranır — sağ üstteki basılı form numarası (sayfanın sağ %30'u) bu aramaya girmez.
///  • Tarih: formun altındaki "TARİH 23/07/2026" (ya da "TARİH" + aynı hizada "04,07"); yoksa personel tablosundaki ilk tarih.
///    Yıl okunmaz.
/// </summary>
public static class AzureFormParser
{
    /// <summary>Aynı satır sayılmak için en fazla dikey fark (sayfa yüksekliğine oranla).</summary>
    private const double RowTolerance = 0.012;

    private static readonly Regex[] StoreLabels =
    {
        new(@"(Ç|C)A(Ğ|G)R(I|İ)\s*B(I|İ)LD(I|İ)REN\s*:?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(I|İ)Ş(I|İ)N\s*YER(I|İ)\s*:?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"M(Ü|U)ŞTER(I|İ)(\s*ADI)?\s*:?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };
    private static readonly Regex AnyLabel = new(@"(Ç|C)A(Ğ|G)R(I|İ)|(I|İ)Ş(I|İ)N\s*YER|ADRES(I|İ)|TEL\s*:|FAX|E-?POSTA|M(Ü|U)ŞTER(I|İ)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeInParens = new(@"\(\s*(\d{3,5})\s*\)?", RegexOptions.Compiled);
    private static readonly Regex DateLabel = new(@"TAR(I|İ)H\s*:?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DayMonth = new(@"(?<!\d)(\d{1,2})\s*[./\-,]\s*(\d{1,2})(?!\d{2})", RegexOptions.Compiled);
    private static readonly Regex Title = new(@"FORMU", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string? Code, string? Name) Store(IReadOnlyList<OcrLine> lines)
    {
        string? code = null, name = null;
        foreach (var label in StoreLabels)
        {
            foreach (var line in lines.Where(l => label.IsMatch(l.Text)).OrderBy(l => l.Y))
            {
                var value = ValueAfter(line, label.Match(line.Text), lines);
                if (value.Length == 0) continue;
                var m = CodeInParens.Match(value);
                if (m.Success)
                {
                    code ??= m.Groups[1].Value;
                    value = value.Remove(m.Index, m.Length).Trim(' ', ',', '.', '-');
                }
                if (name is null && value.Length >= 2) name = value;
                break;
            }
            if (name is not null) break;
        }

        // Elle yazılmış kod (parantezsiz): başlıktan ("... FORMU") önce, sağ üstteki basılı form numarası bölgesinin dışında
        if (code is null)
        {
            var titleY = lines.Where(l => Title.IsMatch(l.Text)).Select(l => l.Y).DefaultIfEmpty(0.25).Min();
            code = lines.Where(l => l.Y < titleY && l.X < 0.70)
                        .Select(l => Regex.Match(l.Text.Trim(), @"^\(?(\d{3,5})\)?$"))
                        .Where(m => m.Success).Select(m => m.Groups[1].Value).FirstOrDefault();
        }
        return (code, name);
    }

    public static (int? Day, int? Month) Date(IReadOnlyList<OcrLine> lines)
    {
        // 1) en alttaki "TARİH ..." satırı (servis elemanı / mağaza onay arasındaki); değer aynı hizada ayrı satır da olabilir
        foreach (var line in lines.Where(l => DateLabel.IsMatch(l.Text)).OrderByDescending(l => l.Y))
        {
            var value = ValueAfter(line, DateLabel.Match(line.Text), lines);
            var m = DayMonth.Match(value);
            if (m.Success && Valid(m, out var d, out var mo)) return (d, mo);
        }
        // 2) personel tablosundaki ilk tarih ("23.07.26")
        foreach (var line in lines.OrderBy(l => l.Y))
        {
            var m = Regex.Match(line.Text, @"(?<!\d)(\d{1,2})\s*[./]\s*(\d{1,2})\s*[./]\s*(\d{2}|\d{4})(?!\d)");
            if (m.Success && Valid(m, out var d, out var mo)) return (d, mo);
        }
        return (null, null);
    }

    /// <summary>Etiketten sonraki metin + aynı hizada, etiketin sağındaki (başka etiket olmayan) satırlar.</summary>
    private static string ValueAfter(OcrLine line, Match label, IReadOnlyList<OcrLine> lines)
    {
        var own = line.Text[(label.Index + label.Length)..].Trim(' ', ':', '.');
        var right = lines.Where(l => !ReferenceEquals(l, line) && Math.Abs(l.Y - line.Y) <= RowTolerance && l.X > line.X
                                     && !AnyLabel.IsMatch(l.Text) && !DateLabel.IsMatch(l.Text))
                         .OrderBy(l => l.X).Select(l => l.Text.Trim());
        return string.Join(' ', new[] { own }.Concat(right).Where(s => s.Length > 0)).Trim();
    }

    private static bool Valid(Match m, out int day, out int month)
    {
        day = int.Parse(m.Groups[1].Value);
        month = int.Parse(m.Groups[2].Value);
        return day is >= 1 and <= 31 && month is >= 1 and <= 12;
    }
}
