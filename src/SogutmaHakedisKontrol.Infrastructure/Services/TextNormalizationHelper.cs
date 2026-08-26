using System.Text;
using System.Text.RegularExpressions;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>Türkçe metin normalizasyonu — mağaza kodu/adı eşleştirmesinde kullanılır.
/// MaterialMatchingService'teki normalize mantığıyla aynı prensiptedir (ayrı tutulmuştur, birbirini etkilemez).</summary>
public static class TextNormalizationHelper
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonAlnumRegex = new(@"[^a-z0-9]", RegexOptions.Compiled);

    public static string NormalizeName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            var mapped = c switch
            {
                'İ' or 'I' => 'i',
                'ı' => 'i',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ğ' or 'ğ' => 'g',
                'Ö' or 'ö' => 'o',
                'Ü' or 'ü' => 'u',
                _ when char.IsUpper(c) => char.ToLowerInvariant(c),
                _ => c,
            };
            if (mapped is '.' or ',' or ';' or ':' or '(' or ')' or '[' or ']')
                continue;
            sb.Append(mapped);
        }
        return WhitespaceRegex.Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Mağaza kodu normalizasyonu: boşluk/tire/sıfır dolgusu farklarını yok sayar. "33-20" ve "3320" farklı kalır
    /// (rakamlar arasındaki tire anlamlı olabilir) — yalnızca boşluk ve baştaki/sondaki gereksiz karakterler temizlenir.</summary>
    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var lowered = NormalizeName(code);
        return NonAlnumRegex.Replace(lowered.Replace("-", ""), "");
    }

    /// <summary>Mağaza kimliği anahtarı: kod varsa normalize edilmiş kod, yoksa normalize edilmiş ad
    /// kullanılır. Excel satırları (StoreCode/StoreName) ve AI'nin okuduğu ham mağaza bilgisi
    /// (StoreCodeRaw/StoreNameRaw) arasında tutarlı karşılaştırma için tek bir yerden üretilir.</summary>
    public static string StoreKey(string? code, string? name)
    {
        var normCode = NormalizeCode(code);
        return !string.IsNullOrEmpty(normCode) ? normCode : NormalizeName(name);
    }

    public static double SimilarityRatio(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        int distance = LevenshteinDistance(a, b);
        int maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)distance / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[a.Length, b.Length];
    }
}
