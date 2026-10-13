using System.Globalization;
using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application;

/// <summary>
/// Soğutma "Glikol Kullanım" hakedişinin sabitleri ve küçük yardımcıları.
/// Glikol hakedişinde SERVİS ÜCRETİ YOKTUR; her servis formu için tek satır vardır: glikol kalemi otomatik eklenir,
/// kullanıcı yalnızca miktarı düzenler; mağaza, tarih ve form numarasını girer/seçer. Malzeme kodu, fatura dönemi,
/// mağaza kodu/adı/formatı sistemden otomatik gelir.
/// </summary>
public static class GlycolClaim
{
    /// <summary>Soğutma fiyat listesindeki glikol kaleminin adının başlangıcı.</summary>
    public const string PriceItemNamePrefix = "İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL";

    /// <summary>Fiyat listesinde kalemin poz/malzeme kodu (adı değişse bile bulunabilsin diye yedek ölçüt).</summary>
    public const string FallbackItemCode = "171";

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static bool IsGlycol(MechanicalDiscipline discipline, CoolingClaimCategory? category)
        => discipline == MechanicalDiscipline.Cooling && category == CoolingClaimCategory.GlycolUsage;

    public static bool IsGlycolPriceName(string? description)
        => !string.IsNullOrWhiteSpace(description)
           && description.Trim().ToUpper(Tr).StartsWith(PriceItemNamePrefix, StringComparison.Ordinal);

    /// <summary>Fiyat listesi adı "ad + tip" birleşimidir ("... GLİKOL GLİKOL"); sondaki tekrarlanan kelime atılır.</summary>
    public static string CleanItemName(string? description)
    {
        var tokens = (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count >= 2 && string.Equals(tokens[^1], tokens[^2], StringComparison.OrdinalIgnoreCase))
            tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }

    /// <summary>Fatura dönemi: hakediş ayının Türkçe büyük harfli adı (6 → "HAZİRAN").</summary>
    public static string PeriodName(int month)
        => month is >= 1 and <= 12 ? Tr.DateTimeFormat.GetMonthName(month).ToUpper(Tr) : string.Empty;

    /// <summary>
    /// Mağaza kodu sıralaması: ikisi de sayıysa sayısal (1761 &lt; 3239 &lt; 10227), değilse metin olarak.
    /// Kodu olmayan (mağazası seçilmemiş) satırlar en sona gider.
    /// </summary>
    public static int CompareStoreCodes(string? a, string? b)
    {
        var aEmpty = string.IsNullOrWhiteSpace(a);
        var bEmpty = string.IsNullOrWhiteSpace(b);
        if (aEmpty || bEmpty) return aEmpty == bEmpty ? 0 : (aEmpty ? 1 : -1);
        if (long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var x)
            && long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var y))
            return x.CompareTo(y);
        return string.Compare(a, b, Tr, CompareOptions.IgnoreCase);
    }
}
