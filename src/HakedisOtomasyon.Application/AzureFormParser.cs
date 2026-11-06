using System.Text.RegularExpressions;

namespace HakedisOtomasyon.Application;

/// <summary>OCR'ın okuduğu bir satır: metin + sayfadaki konumu (sol üst köşeye göre 0–1 arası, satırın ortası).</summary>
public sealed record OcrLine(string Text, double X, double Y);

/// <summary>OCR'ın okuduğu bir tablo hücresi; <paramref name="Confidence"/> hücredeki kelimelerin en düşük güveni (0–1).</summary>
public sealed record OcrCell(int Row, int Column, string Text, double Confidence);

/// <summary>OCR'ın okuduğu bir tablo.</summary>
public sealed record OcrTable(IReadOnlyList<OcrCell> Cells);

/// <summary>Servis formunun "Kullanılan Malzeme" tablosundan okunan gaz miktarı.</summary>
/// <param name="Quantity">Okunan miktar (kg); okunamadıysa null.</param>
/// <param name="RawText">Formda yazan miktar metni ("25kg").</param>
/// <param name="GasType">Formda yazan gaz türü ("R404"); yazmıyorsa null.</param>
/// <param name="NeedsCheck">Miktar kesin okunamadı (bozuk/silik yazı, birden çok gaz satırı ...) — kullanıcı teyit etmeli.</param>
/// <param name="Note">Teyit nedeni.</param>
public sealed record GasReading(decimal? Quantity, string? RawText, string? GasType, bool NeedsCheck, string? Note);

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

    private static readonly Regex GasWord = new(@"\bGAZ\b|\bR\s*-?\s*(404|22|407|410|134|448|449|452|507|290|600)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GasTypeRx = new(@"R\s*-?\s*(404|22|407|410|134|448|449|452|507|290|600)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CleanQty = new(@"^\s*(\d{1,3}(?:[.,]\d{1,2})?)\s*(KG|KĞ|K|KGR)?\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyNumber = new(@"\d{1,3}(?:[.,]\d{1,2})?", RegexOptions.Compiled);

    /// <summary>En az bu güvenle okunan miktar teyitsiz kabul edilir.</summary>
    private const double QtyConfidence = 0.80;

    /// <summary>
    /// "Kullanılan Malzeme" tablosunda (başlığında "Malzemenin Cinsi" + "Miktarı" olan, ATIK tablosu olmayan) adında "gaz" ya da
    /// "R404/R22..." geçen satır(lar)ın miktarı. Tablo iki yarıdır (1–10 | 11–20): miktar, gaz hücresinin SAĞINDAKİ ilk "Miktarı"
    /// sütunundadır. Temiz yazılmamış, düşük güvenle okunmuş ya da birden çok satıra dağılmış miktar teyit istenerek döner.
    /// </summary>
    public static GasReading? Gas(IReadOnlyList<OcrTable> tables)
    {
        foreach (var table in tables)
        {
            var header = table.Cells.Where(c => c.Row == 0).ToList();
            var headerText = string.Join(" ", header.Select(c => c.Text));
            if (!headerText.Contains("Cins", StringComparison.OrdinalIgnoreCase) || !headerText.Contains("Miktar", StringComparison.OrdinalIgnoreCase)
                || headerText.Contains("ATIK", StringComparison.OrdinalIgnoreCase)) continue;
            var qtyColumns = header.Where(c => c.Text.Contains("Miktar", StringComparison.OrdinalIgnoreCase)).Select(c => c.Column).OrderBy(c => c).ToList();

            var found = new List<(decimal? Qty, string Raw, bool Clean, string? Type)>();
            foreach (var cell in table.Cells.Where(c => c.Row > 0 && !qtyColumns.Contains(c.Column) && GasWord.IsMatch(c.Text)))
            {
                var qtyCol = qtyColumns.Where(c => c > cell.Column).DefaultIfEmpty(-1).Min();
                var qtyCell = table.Cells.FirstOrDefault(c => c.Row == cell.Row && c.Column == qtyCol);
                var raw = qtyCell?.Text.Trim() ?? string.Empty;
                var clean = CleanQty.Match(raw);
                var num = clean.Success ? clean.Groups[1].Value : AnyNumber.Match(raw) is { Success: true } m ? m.Value : null;
                decimal? qty = num is not null && decimal.TryParse(num.Replace(',', '.'), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var q) && q > 0 ? q : null;
                var isClean = clean.Success && qtyCell!.Confidence >= QtyConfidence && qty is > 0 and <= 200;
                var t = GasTypeRx.Match(cell.Text);
                found.Add((qty, raw, isClean, t.Success ? "R" + t.Groups[1].Value : null));
            }
            if (found.Count == 0) continue;

            var type = found.Select(f => f.Type).FirstOrDefault(t => t is not null);
            if (found.Count > 1)
            {
                var sum = found.All(f => f.Qty.HasValue) ? found.Sum(f => f.Qty!.Value) : (decimal?)null;
                return new(sum, string.Join(" + ", found.Select(f => f.Raw)), type, true,
                    $"Formda {found.Count} gaz satırı var ({string.Join(" + ", found.Select(f => f.Raw.Length > 0 ? f.Raw : "?"))}); toplam yazıldı.");
            }
            var one = found[0];
            if (one.Qty is null)
                return new(null, one.Raw, type, true, one.Raw.Length == 0 ? "Gaz satırı var ama miktar okunamadı." : $"Miktar okunamadı (\"{one.Raw}\").");
            return one.Clean
                ? new(one.Qty, one.Raw, type, false, null)
                : new(one.Qty, one.Raw, type, true, $"Miktar net okunamadı (formda \"{one.Raw}\").");
        }
        return null;
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
