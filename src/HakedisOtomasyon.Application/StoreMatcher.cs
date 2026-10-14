using System.Globalization;
using System.Text;
using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application;

/// <summary>Servis formundan (el yazısı, yapay zekâ ile) okunan mağaza kodu/adı.</summary>
/// <param name="StoreCode">Okunan mağaza kodu (yoksa null).</param>
/// <param name="StoreName">Okunan mağaza adı (yoksa null).</param>
/// <param name="Error">Okuma yapılamadıysa açıklama.</param>
public sealed record HandwrittenStoreResult(string? StoreCode, string? StoreName, string? Error);

/// <summary>Servis formundaki el yazısı mağaza bilgisini okur (Gemini). Çok sayfalı PDF'de yalnız istenen sayfa gönderilir.</summary>
public interface IHandwrittenStoreReader
{
    /// <summary>Yapay zekâ ayarlı mı (API anahtarı var mı)?</summary>
    bool IsConfigured { get; }

    /// <param name="pageIndex">Okunacak sayfa (0 tabanlı; görselde 0).</param>
    Task<HandwrittenStoreResult> ReadAsync(string absoluteFilePath, int pageIndex = 0, CancellationToken cancellationToken = default);
}

/// <summary>Okunan mağaza kodu/adının mağaza listesiyle eşleştirilmesi.</summary>
/// <param name="Selected">Güvenle seçilebilen mağaza (yoksa null).</param>
/// <param name="Suggestions">Emin olunamadığında kullanıcıya gösterilecek en yakın mağazalar (en fazla 3).</param>
/// <param name="Method">"kod", "ad" ya da boş.</param>
public sealed record StoreMatchResult(StoreDto? Selected, IReadOnlyList<StoreDto> Suggestions, string Method);

/// <summary>
/// El yazısından okunan mağaza bilgisini mağaza listesiyle eşleştirir. Yanlış mağaza SESSİZCE seçilmez:
///  • Kod okunduysa ve listede o kodlu mağaza varsa → o mağaza (denemede kod okunan formların hepsi doğruydu).
///  • Yalnız ad okunduysa → ayırt edici kelimeler (MİGROS, MM, AVM ... gibi genel kelimeler hariç) mağaza adı + iliyle
///    karşılaştırılır. Tek ve belirgin biçimde öne çıkan mağaza varsa seçilir; yoksa en yakın 3 mağaza öneri olarak döner.
/// Türkçe karakterler ve büyük/küçük harf farkı yok sayılır (AI "Demircioğlu"/"MIGROS" yazabilir).
/// </summary>
public static class StoreMatcher
{
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "MIGROS", "MM", "MMM", "M", "AVM", "MAGAZA", "MAGAZASI", "SUBE", "SUBESI", "CAD", "CADDE", "CADDESI", "MH", "MAH",
        "MAHALLE", "MAHALLESI", "SK", "SOK", "SOKAK", "BULVAR", "BULVARI", "BLV", "VE", "AS", "TIC", "TICARET", "MARKET",
        "JET", "MJET", "MACROCENTER", "MACRO", "CENTER", "S", "K", "N",
    };

    /// <summary>Bir kelimenin "eşleşti" sayılması için en düşük benzerlik; önerilere girmek için en düşük puan.</summary>
    private const double StrongMatch = 0.85, SuggestScore = 0.40;

    /// <summary>Puanla seçim: en az bu puan ve ikinciyle en az bu fark.</summary>
    private const double SelectScore = 0.90, SelectMargin = 0.15;

    /// <summary>Formda yazan biçim (MM, 5M ...) mağazanınkiyle aynıysa ek puan; "MİGROS" yazıyorsa ve mağaza adında da varsa ek puan.</summary>
    private const double FormatBonus = 0.20, MigrosBonus = 0.10;

    /// <summary>İl kelimeleri adın yerini tutmaz: aynı ildeki tüm mağazalar eşleşir. Bu yüzden il benzerliği düşük ağırlıklıdır.</summary>
    private const double CityWeight = 0.6;

    /// <summary>Mağaza biçimleri (formda elle yazılır; mağaza kaydında Format alanındadır).</summary>
    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal) { "M", "MM", "MMM", "2M", "3M", "4M", "5M", "MJET", "MION" };

    /// <summary>Migros dışı markalar: formda "MİGROS" yazıyorsa bu mağazalar otomatik seçilmez.</summary>
    private static readonly string[] OtherBrands = { "MJET", "MION", "MACRO", "MACROCENTER", "MACROKIOSK" };

    /// <param name="preferredRegion">Hakedişin bölgesi (örn. "İÇ ANADOLU" — hakedişte kullanılan mağazalardan). Verilirse ad
    /// eşleşmesi önce o bölgedeki mağazalarda aranır; orada bulunamazsa tüm listede. Kod eşleşmesi bölgeden bağımsızdır.</param>
    public static StoreMatchResult Match(string? code, string? name, IReadOnlyList<StoreDto> stores, string? preferredRegion = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredRegion))
        {
            var codeHit = MatchCore(code, null, stores);
            if (codeHit.Selected is not null) return codeHit;
            var inRegion = stores.Where(s => string.Equals(s.Region?.Trim(), preferredRegion.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (inRegion.Count > 0)
            {
                var regional = MatchCore(null, name, inRegion);
                if (regional.Selected is not null || regional.Suggestions.Count > 0) return regional;
            }
        }
        return MatchCore(code, name, stores);
    }

    private static StoreMatchResult MatchCore(string? code, string? name, IReadOnlyList<StoreDto> stores)
    {
        // 1) Kod: listede varsa o mağaza
        var digits = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length > 0)
        {
            var byCode = stores.FirstOrDefault(s => string.Equals(s.Code?.Trim(), digits, StringComparison.OrdinalIgnoreCase));
            if (byCode is not null) return new(byCode, Array.Empty<StoreDto>(), "kod");
        }

        // 2) Ad: ayırt edici kelimeler (genel kelimeler, biçim ve 3 harften kısa parçalar hariç) + yazılan biçim + "MİGROS" ibaresi
        var all = Tokens(JoinFormat(name)).ToList();
        var wanted = all.Where(t => t.Length >= 3 && !Generic.Contains(t) && !Formats.Contains(t)).Distinct().ToList();
        if (wanted.Count == 0) return new(null, Array.Empty<StoreDto>(), string.Empty);
        var writtenFormat = all.LastOrDefault(Formats.Contains) switch { "2M" => "MM", "3M" => "MMM", var f => f };   // 2M = MM, 3M = MMM
        var saysMigros = all.Contains("MIGROS");

        var prepared = stores.Select(s => (Store: s,
                Name: WithJoinedPairs(Tokens(s.Name).Where(t => !Generic.Contains(t)).ToList()),
                City: Tokens(s.City).ToList(),
                Format: FormatToken(s.Format),
                Brand: OtherBrands.Any(b => Tokens(s.Name).FirstOrDefault() == b) ? "OTHER" : "MIGROS",
                HasMigros: Tokens(s.Name).Contains("MIGROS")))
            .ToList();

        // Formda yazanla ÇELİŞEN mağaza otomatik seçilmez: biçim farklıysa (MM yazılmış, mağaza M-JET) ya da formda "MİGROS"
        // yazarken mağaza başka markaysa (MJET/MION/MACRO).
        bool Compatible((StoreDto Store, List<string> Name, List<string> City, string? Format, string Brand, bool HasMigros) p)
            => !(writtenFormat is not null && p.Format is not null && p.Format != writtenFormat)
               && !(saysMigros && p.Brand == "OTHER");

        // 2a) Okunan bir kelime listede YALNIZ BİR mağazanın adında geçiyorsa güçlü kanıttır ("DEMİRCİOĞLU"). Bu tür kelimelerin
        //     hepsi aynı mağazayı gösteriyorsa ve mağaza formda yazanla çelişmiyorsa seçilir.
        var unique = new HashSet<StoreDto>();
        foreach (var w in wanted)
        {
            var hits = prepared.Where(p => p.Name.Any(t => Similarity(w, t) >= StrongMatch)).Take(2).ToList();
            if (hits.Count == 1 && Compatible(hits[0])) unique.Add(hits[0].Store);
        }
        if (unique.Count == 1) return new(unique.First(), Array.Empty<StoreDto>(), "ad");

        // 2b) Puan: ad benzerliği + biçim/Migros uyumu. Tek ve belirgin biçimde öne çıkan, çelişmeyen mağaza seçilir.
        var scored = prepared
            .Select(p => (p.Store, Ok: Compatible(p), Score: Score(wanted, p.Name, p.City)
                          + (writtenFormat is not null && p.Format == writtenFormat ? FormatBonus : 0d)
                          + (saysMigros && p.HasMigros ? MigrosBonus : 0d)))
            .Where(x => x.Score >= SuggestScore)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Store.Code, Comparer<string>.Create(GlycolClaim.CompareStoreCodes))
            .Take(4)
            .ToList();
        if (scored.Count == 0) return new(null, Array.Empty<StoreDto>(), string.Empty);

        var top = scored[0];
        var second = scored.Count > 1 ? scored[1].Score : 0d;
        if (top.Ok && top.Score >= SelectScore && top.Score - second >= SelectMargin)
            return new(top.Store, Array.Empty<StoreDto>(), "ad");

        // 2c) Emin olunamadı: en yakın 3 mağaza öneri olarak döner (kullanıcı seçer)
        return new(null, scored.Take(3).Select(x => x.Store).ToList(), "ad");
    }

    /// <summary>Mağaza adı kelimelerine yan yana iki kelimenin bitişik halini de ekler: formda "Parksite" yazılabilir,
    /// listede "PARK SİTE" geçer.</summary>
    private static List<string> WithJoinedPairs(List<string> tokens)
    {
        for (int i = 0, n = tokens.Count; i + 1 < n; i++) tokens.Add(tokens[i] + tokens[i + 1]);
        return tokens;
    }

    /// <summary>"5 M" gibi ayrı yazılmış biçimi birleştirir ("5M").</summary>
    private static string? JoinFormat(string? text)
        => text is null ? null : System.Text.RegularExpressions.Regex.Replace(text, @"\b([2-5])\s+[Mm]\b", "$1M");

    /// <summary>Mağaza kaydındaki biçimi tek kelimeye indirger: "M-JET" → MJET, "SERVİS REYONLU M" → M, "M ANADOLU" → M, "5M" → 5M.</summary>
    private static string? FormatToken(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        var compact = Fold(format).Replace("-", string.Empty);
        if (compact.Contains("MJET")) return "MJET";
        if (compact.Contains("MION")) return "MION";
        return Tokens(format.Replace("-", string.Empty)).LastOrDefault(Formats.Contains);
    }

    /// <summary>Okunan her ayırt edici kelimenin mağaza adı (tam ağırlık) ya da ili (düşük ağırlık) içindeki en iyi
    /// benzerliğinin ortalaması (0–1).</summary>
    private static double Score(List<string> wanted, List<string> nameTokens, List<string> cityTokens)
    {
        double total = 0;
        foreach (var w in wanted)
        {
            var byName = nameTokens.Count == 0 ? 0d : nameTokens.Max(t => Similarity(w, t));
            var byCity = cityTokens.Count == 0 ? 0d : cityTokens.Max(t => Similarity(w, t)) * CityWeight;
            total += Math.Max(byName, byCity);
        }
        return total / wanted.Count;
    }

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1d;
        // kısaltma: formda yazan kelime mağaza adındaki kelimenin BAŞI olabilir ("GOP" ↔ "G.O.PAŞA" → GOPASA).
        // Tersi geçerli DEĞİL: mağaza adındaki kısa kelime ("DERE") formdaki uzun kelimenin ("DERECİKLER") kısaltması sayılmaz.
        if (a.Length >= 3 && b.StartsWith(a, StringComparison.Ordinal)) return 0.85;
        if (a.Length < 4 || b.Length < 4) return 0d;
        var ratio = 1d - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        return ratio >= 0.75 ? ratio * 0.9 : 0d;   // yazım hatası payı (DEMIRCIOGLU ↔ DEMIRCIOGU)
    }

    /// <summary>Türkçe karakterleri sadeleştirip büyük harfe çevirir; harf/rakam dışını ayırıcı sayar. "G.O.PAŞA" → "GOPASA".</summary>
    public static IEnumerable<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var folded = Fold(text);
        // noktalı kısaltmalar birleştirilir: "G.O.PAŞA" → "GOPASA", "K. DEMİRCİOĞLU" → "K DEMIRCIOGLU"
        folded = System.Text.RegularExpressions.Regex.Replace(folded, @"(?<=\b[A-Z])\.(?=[A-Z])", string.Empty);
        var sb = new StringBuilder();
        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Fold(string text)
    {
        var upper = text.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        var sb = new StringBuilder(upper.Length);
        foreach (var ch in upper)
            sb.Append(ch switch
            {
                'İ' or 'I' or 'Î' => 'I',
                'Ş' => 'S', 'Ğ' => 'G', 'Ü' or 'Û' => 'U', 'Ö' => 'O', 'Ç' => 'C', 'Â' => 'A',
                _ => ch,
            });
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
