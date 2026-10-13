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

    // ── Gaz Kullanım hakedişi: Glikol ile AYNI yapı (servis ücreti yok, form başına tek satır, aynı Excel düzeni).
    //    Tek fark: glikol tek kalemdir ve otomatik gelir; gazda kalem (R22, R404, R407C, R410A, R134a ...) satır başına SEÇİLİR.

    /// <summary>Soğutma fiyat listesindeki soğutucu gaz kalemlerinin adının başlangıcı ("SOĞUTUCU GAZ 10,9 KG TÜP (kg fiyatı) R404").</summary>
    public const string GasPriceItemNamePrefix = "SOĞUTUCU GAZ";

    public static bool IsGas(MechanicalDiscipline discipline, CoolingClaimCategory? category)
        => discipline == MechanicalDiscipline.Cooling && category == CoolingClaimCategory.GasUsage;

    public static bool IsGasPriceName(string? description)
        => !string.IsNullOrWhiteSpace(description)
           && description.Trim().ToUpper(Tr).StartsWith(GasPriceItemNamePrefix, StringComparison.Ordinal);

    /// <summary>"Kullanım" hakedişi mi (Glikol ya da Gaz)? İkisi de aynı tek ekranı (GlycolFormsTable) ve aynı Excel düzenini kullanır.</summary>
    public static bool IsUsageClaim(MechanicalDiscipline discipline, CoolingClaimCategory? category)
        => IsGlycol(discipline, category) || IsGas(discipline, category);

    /// <summary>Çıktı başlığının bölgeden sonraki kısmı ("İÇ ANADOLU " + bu + " HAKEDİŞİ" / " İCMALİ").</summary>
    public static string TitleCore(CoolingClaimCategory? category) => category == CoolingClaimCategory.GasUsage
        ? "MİGROS SATIŞ MAĞAZALARI SOĞUTMA GAZ KULLANIM"
        : "MİGROS SATIŞ MAĞAZALARI SOĞUTMA GLİKOL KULLANIM";

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
