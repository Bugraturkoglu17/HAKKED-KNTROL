using System.Text.RegularExpressions;

namespace HakedisOtomasyon.Application;

/// <summary>OCR'ın okuduğu bir satır: metin + sayfadaki konumu (sol üst köşeye göre 0–1 arası, satırın ortası).</summary>
public sealed record OcrLine(string Text, double X, double Y);

/// <summary>OCR'ın okuduğu bir hücredeki kelime ve güveni (0–1).</summary>
public sealed record OcrWord(string Text, double Confidence);

/// <summary>OCR'ın okuduğu bir tablo hücresi; <paramref name="Confidence"/> hücredeki kelimelerin en düşük güveni (0–1).</summary>
public sealed record OcrCell(int Row, int Column, string Text, double Confidence)
{
    /// <summary>Hücredeki kelimeler ve ayrı ayrı güvenleri (bilinmiyorsa null). Bozuk okunan bir kelime ("Glika") aynı hücredeki
    /// net kelimenin ("80kg") güvenini düşürmesin diye miktar güveni buradan alınır.</summary>
    public IReadOnlyList<OcrWord>? Words { get; init; }
}

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
    private static readonly Regex BareCode = new(@"(?<![\d.,/])(\d{3,5})(?![\d.,/])", RegexOptions.Compiled);
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
                if (!m.Success)
                {
                    // parantezsiz yazılmış kod: satırdaki SON 3–5 haneli sayı ("365 Zirve Kent 3M Migros 1996" → 1996)
                    var bare = BareCode.Matches(value);
                    if (bare.Count > 0) m = bare[^1];
                }
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
        code ??= TopHandwrittenCode(lines);
        return (code, name);
    }

    /// <summary>
    /// Mağaza kodu adayları, güvenilirlik sırasıyla: <see cref="Store"/>'un kodu, sayfanın üstündeki el yazısı kod, sonra
    /// "ÇAĞRI BİLDİREN / İŞİN YERİ / MÜŞTERİ" satırlarında geçen diğer 3–5 haneli sayılar. Ad okunabilse bile üstteki el yazısı kod
    /// ve "İŞİN YERİ: … Migros 4280" gibi satırlar kod için kullanılır; eşleştirme listede olmayanı eler.
    /// </summary>
    public static IReadOnlyList<string> StoreCodes(IReadOnlyList<OcrLine> lines)
    {
        var codes = new List<string>();
        void Add(string? c) { if (!string.IsNullOrEmpty(c) && !codes.Contains(c)) codes.Add(c); }

        Add(Store(lines).Code);
        Add(TopHandwrittenCode(lines));
        foreach (var label in StoreLabels)
            foreach (var line in lines.Where(l => label.IsMatch(l.Text)).OrderBy(l => l.Y))
            {
                var value = ValueAfter(line, label.Match(line.Text), lines);
                var m = CodeInParens.Match(value);
                if (m.Success) { Add(m.Groups[1].Value); continue; }
                var bare = BareCode.Matches(value);
                if (bare.Count > 0) Add(bare[^1].Groups[1].Value);
            }
        return codes;
    }

    /// <summary>
    /// Başlığın ("... FORMU") üstünde, form numarası bölgesi dışında, tek başına duran el yazısı 3–5 haneli sayı.
    /// El yazısı OCR'ı aralık/nokta ekleyebilir ("4 280", "4.280", "(4280)"); bunlar silinip bakılır. Telefon, ISO numarası gibi
    /// basılı metinler başka sözcük içerdiği ya da hane sayısı tutmadığı için eşleşmez.
    /// </summary>
    private static string? TopHandwrittenCode(IReadOnlyList<OcrLine> lines)
    {
        var titleY = lines.Where(l => Title.IsMatch(l.Text)).Select(l => l.Y).DefaultIfEmpty(0.25).Min();
        return lines.Where(l => l.Y < titleY && l.X < 0.70)
                    .Select(l => Regex.Replace(l.Text, @"[\s.,()\-]", string.Empty))
                    .Where(t => Regex.IsMatch(t, @"^\d{3,5}$"))
                    .FirstOrDefault();
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

    private static readonly Regex GasWord = new(@"\b[G6]\s*[AÂ4]\s*[ZŽ2]\b|\bR\s*-?\s*(4\s*[0O]\s*[4AhH]|22|407|410|134|448|449|452|507|290|600)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GasTypeRx = new(@"R\s*-?\s*(4\s*[0O]\s*[4AhH]|22|407|410|134|448|449|452|507|290|600)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CleanQty = new(@"^\s*(\d{1,3}(?:[.,]\d{1,2})?)\s*(KG|KĞ|K|KGR)?\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KgQty = new(@"\d\s*(KG|KĞ|K\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
                found.Add((qty, raw, isClean, t.Success ? "R" + NormalizeGas(t.Groups[1].Value) : null));
            }
            if (found.Count == 0)
            {
                // yalnız kilo cinsinden miktar (gaz kg ile yazılır; "2 ad" gibi adet satırları gaz sayılmaz)
                var filled = table.Cells.Where(c => c.Row > 0 && qtyColumns.Contains(c.Column) && KgQty.IsMatch(c.Text)).ToList();
                if (filled.Count == 1)
                {
                    var only = filled[0];
                    var nameCell = table.Cells.Where(c => c.Row == only.Row && c.Column < only.Column).OrderByDescending(c => c.Column).FirstOrDefault();
                    var n = AnyNumber.Match(only.Text).Value;
                    decimal? q = decimal.TryParse(n.Replace(',', '.'), System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
                    return new(q, only.Text.Trim(), null, true,
                        $"Malzeme adı gaz olarak okunamadı (\"{nameCell?.Text.Trim()}\"); tek dolu satırın miktarı alındı.");
                }
                continue;
            }

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

    private static string NormalizeGas(string raw)
    {
        var t = raw.Replace(" ", string.Empty).ToUpperInvariant().Replace('O', '0');
        return t.StartsWith("40") ? "404" : t;
    }

    // El yazısında "Glikol" çoğu zaman bozuk okunur ("Glika", "Gilla", "Glikol"); G + l/i + k/c/l harfleri aranır. Gaz ("Gaz") eşleşmez.
    private static readonly Regex GlycolWord = new(@"(?<![A-Za-zÇĞİÖŞÜçğıöşü])G[lLiI1]{1,2}[kKcCgGlL][A-Za-zçğıöşü]{0,3}(?![A-Za-zÇĞİÖŞÜçğıöşü])|ANT[İI]FR[İI]Z|PROP[İI]LEN|SO[ĞG]UTKAN", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KgInText = new(@"(?<!\d)(\d{1,3}(?:[.,]\d{1,2})?)\s*(KG|KĞ|KGR|KQ)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BidonInText = new(@"(?<!\d)(\d{1,2})\s*(B[İI]DON|BDN|BD[A-Z]{0,3}|B[İI]D[A-Z]{0,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Glikol bidonunun kg'ı (gerçek formlarda: 4 bidon = 80 kg, 2 bidon = 40 kg).</summary>
    private const decimal KgPerBidon = 20m;

    private static decimal? ParseQty(string s)
        => decimal.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var q) && q > 0 ? q : null;

    /// <summary>"80kg" eşleşmesini içeren kelimenin(lerin) güveni; kelime bilgisi yoksa hücrenin genel güveni.</summary>
    private static double WordConfidence(OcrCell cell, Match qty)
    {
        if (cell.Words is not { Count: > 0 } words) return cell.Confidence;
        var digits = qty.Groups[1].Value;
        var hit = words.Where(w => w.Text.Contains(digits, StringComparison.Ordinal) || w.Text.Trim().Equals("kg", StringComparison.OrdinalIgnoreCase)).ToList();
        return hit.Count == 0 ? cell.Confidence : hit.Min(w => w.Confidence);
    }

    /// <summary>
    /// "Kullanılan Malzeme" tablosunda adı "glikol" olan (bozuk yazılmış olabilir) satır(lar)ın miktarı, kg cinsinden.
    /// Miktar iki yerde yazılabilir: malzeme adı hücresinde ("Glikol 80kg") ya da sağdaki "Miktarı" sütununda ("80 kg" / "4 bidon").
    /// Önce "kg" olarak yazılan alınır (net okunduysa teyitsiz); yalnız bidon yazıyorsa bidon × 20 kg alınır ve teyit istenir;
    /// düz sayı ya da okunamayan miktar da teyit ister. Birden çok glikol satırı toplanır ve teyit ister.
    /// </summary>
    public static GasReading? Glycol(IReadOnlyList<OcrTable> tables)
    {
        foreach (var table in tables)
        {
            var header = table.Cells.Where(c => c.Row == 0).ToList();
            var headerText = string.Join(" ", header.Select(c => c.Text));
            if (!headerText.Contains("Cins", StringComparison.OrdinalIgnoreCase) || !headerText.Contains("Miktar", StringComparison.OrdinalIgnoreCase)
                || headerText.Contains("ATIK", StringComparison.OrdinalIgnoreCase)) continue;
            var qtyColumns = header.Where(c => c.Text.Contains("Miktar", StringComparison.OrdinalIgnoreCase)).Select(c => c.Column).OrderBy(c => c).ToList();

            var rows = new List<(decimal? Qty, string Raw, bool Clean, string? Note)>();
            foreach (var cell in table.Cells.Where(c => c.Row > 0 && !qtyColumns.Contains(c.Column) && GlycolWord.IsMatch(c.Text)))
            {
                var qtyCol = qtyColumns.Where(c => c > cell.Column).DefaultIfEmpty(-1).Min();
                var qtyCell = table.Cells.FirstOrDefault(c => c.Row == cell.Row && c.Column == qtyCol);
                var qtyText = qtyCell?.Text.Trim() ?? string.Empty;
                var raw = (cell.Text.Trim() + " | " + qtyText).Trim(' ', '|');

                var nameKg = KgInText.Match(cell.Text);
                var cellKg = KgInText.Match(qtyText);
                var bidon = BidonInText.Match(qtyText);
                if (nameKg.Success || cellKg.Success)
                {
                    var useName = nameKg.Success;
                    var qty = ParseQty((useName ? nameKg : cellKg).Groups[1].Value);
                    var conf = useName ? WordConfidence(cell, nameKg) : WordConfidence(qtyCell!, cellKg);
                    var ok = qty is > 0 and <= 500 && conf >= QtyConfidence;
                    // adda "80kg", sağda "4 bidon": tutmuyorsa teyit istenir (bidon çok kötü okunduysa karşılaştırılmaz)
                    var conflict = bidon.Success && (qtyCell?.Confidence ?? 1d) >= 0.5 && ParseQty(bidon.Groups[1].Value) * KgPerBidon != qty;
                    rows.Add((qty, raw, ok && !conflict, ok && !conflict ? null : conflict ? $"Formda \"{qtyText}\" ve \"{cell.Text.Trim()}\" tutmuyor." : $"Miktar net okunamadı (formda \"{raw}\")."));
                }
                else if (bidon.Success)
                {
                    var n = ParseQty(bidon.Groups[1].Value);
                    rows.Add((n * KgPerBidon, raw, false, $"Formda \"{qtyText}\" yazıyor; bidon başına {KgPerBidon:0} kg varsayıldı."));
                }
                else
                {
                    var n = AnyNumber.Match(qtyText);
                    rows.Add((n.Success ? ParseQty(n.Value) : null, raw, false,
                        qtyText.Length == 0 ? "Glikol satırı var ama miktar okunamadı." : $"Miktar birimi okunamadı (formda \"{qtyText}\")."));
                }
            }
            if (rows.Count == 0) continue;

            if (rows.Count > 1)
            {
                var sum = rows.All(r => r.Qty.HasValue) ? rows.Sum(r => r.Qty!.Value) : (decimal?)null;
                return new(sum, string.Join(" + ", rows.Select(r => r.Raw)), null, true,
                    $"Formda {rows.Count} glikol satırı var; toplam yazıldı.");
            }
            var one = rows[0];
            return new(one.Qty, one.Raw, null, !one.Clean, one.Note);
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
