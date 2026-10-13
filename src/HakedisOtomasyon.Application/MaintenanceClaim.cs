using System.Globalization;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application;

/// <summary>
/// Soğutma "Bakım &amp; Onarım" (servis ve bakım malzemeleri) hakedişinin sabitleri ve küçük yardımcıları.
/// Her servis formuna, mağazanın iline göre OTOMATİK bir servis bedeli eklenir:
///   • mağaza ANKARA'daysa  → "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ"  (S1)
///   • Ankara dışındaysa    → "1 EKIP ŞEHİR DIŞI SERVİS BEDELİ" (S2)
/// Ardından kullanıcı servis formundaki malzemeleri ve adam/saat miktarlarını fiyat listesinden ekler.
/// </summary>
public static class MaintenanceClaim
{
    /// <summary>Şehir içi sayılan il. Bunun dışındaki tüm iller "şehir dışı"dır.</summary>
    public const string HomeCity = "ANKARA";

    public const string InCityFeeCode = "S1";
    public const string OutOfCityFeeCode = "S2";
    public const string InCityFeeName = "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ";
    public const string OutOfCityFeeName = "1 EKIP ŞEHİR DIŞI SERVİS BEDELİ";

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public enum ServiceFeeResult
    {
        /// <summary>Doğru servis bedeli zaten vardı.</summary>
        Unchanged,
        /// <summary>Servis bedeli eklendi.</summary>
        Added,
        /// <summary>Mağazanın ili değişti: şehir içi ↔ şehir dışı bedeli değiştirildi.</summary>
        Swapped,
        /// <summary>Mağazanın ili kayıtlı değil — şehir içi/dışı belirlenemedi, bedel eklenmedi.</summary>
        UnknownCity,
        /// <summary>Fiyat listesinde servis bedeli kalemi bulunamadı.</summary>
        MissingPriceItem,
    }

    public static bool IsMaintenance(MechanicalDiscipline discipline, CoolingClaimCategory? category)
        => discipline == MechanicalDiscipline.Cooling && category == CoolingClaimCategory.MaintenanceRepair;

    /// <summary>Soğutma "Kompresör Değişim" hakedişi mi? Bakım &amp; Onarım ile AYNI yapıdadır (form başına şehir içi/dışı servis
    /// bedeli + fiyat listesinden kalemler); ek olarak hakedişe özel kalem (kompresör marka/modeli) ve FATURA + % kalemi girilir.</summary>
    public static bool IsCompressor(MechanicalDiscipline discipline, CoolingClaimCategory? category)
        => discipline == MechanicalDiscipline.Cooling && category == CoolingClaimCategory.CompressorReplacement;

    /// <summary>Hakediş, "form bilgisi + alt alta kalem" tek ekranını (MaintenanceFormsTable) kullanan türlerden mi?</summary>
    public static bool UsesMaterialScreen(MechanicalDiscipline discipline, CoolingClaimCategory? category)
        => IsMaintenance(discipline, category) || IsCompressor(discipline, category);

    /// <summary>Çıktı başlığının bölgeden sonraki kısmı ("İÇ ANADOLU " + bu + " HAKEDİŞİ" / " İCMALİ").</summary>
    public static string TitleCore(CoolingClaimCategory? category) => category == CoolingClaimCategory.CompressorReplacement
        ? "MİGROS SOĞUTMA KOMPRESÖR TEMİN VE DEĞİŞİM"
        : "MİGROS SATIŞ MAĞAZALARI SOĞUTMA SERVİS VE BAKIM MALZEMELERİ";

    /// <summary>
    /// Hakedişe özel kalemin birim fiyatı ve çıktıdaki MALZEME KODU etiketi:
    ///   • Liste fiyatı            → fiyat = tutar                    → "LİSTE FİYATI"
    ///   • İskontolu liste fiyatı  → fiyat = tutar × (1 − %/100)      → "%10 İSKONTOLU LİSTE FİYATI"
    ///   • Fatura + ilave          → fiyat = tutar × (1 + %/100)      → "FATURA +%10 M.K."
    /// Yüzde elle girilir (sabit %10 değildir). Fiyat yuvarlanmaz; çıktı 2 hane gösterir.
    /// </summary>
    public static (decimal UnitPrice, string CodeLabel) CustomItemPricing(CustomPriceBasis basis, decimal baseAmount, decimal percent)
    {
        if (baseAmount <= 0m) throw new InvalidOperationException("Tutar sıfırdan büyük olmalıdır.");
        if (basis != CustomPriceBasis.ListPrice && (percent < 0m || percent > 100m))
            throw new InvalidOperationException("Yüzde 0 ile 100 arasında olmalıdır.");
        var p = percent.ToString("0.##", Tr);
        return basis switch
        {
            CustomPriceBasis.DiscountedListPrice => (baseAmount * (1m - percent / 100m), percent == 0m ? "LİSTE FİYATI" : $"%{p} İSKONTOLU LİSTE FİYATI"),
            CustomPriceBasis.InvoicePlusMarkup => (baseAmount * (1m + percent / 100m), percent == 0m ? "FATURA" : $"FATURA +%{p} M.K."),
            _ => (baseAmount, "LİSTE FİYATI"),
        };
    }

    private static string Norm(string? s)
        => string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpper(Tr);

    /// <summary>Mağaza şehir içinde mi? İl kayıtlı değilse <c>null</c> (karar verilemez).</summary>
    public static bool? IsInCity(string? city)
    {
        var c = Norm(city);
        return c.Length == 0 ? null : c == HomeCity;
    }

    private static bool IsFeeCode(string? code, string wanted)
        => string.Equals(code?.Trim(), wanted, StringComparison.OrdinalIgnoreCase);

    private static bool NameStarts(string? description, string name)
        => Norm(description).StartsWith(name, StringComparison.Ordinal);

    /// <summary>Kalem otomatik servis bedeli mi (şehir içi ya da şehir dışı)?</summary>
    public static bool IsServiceFee(string? itemCode, string? description)
        => IsFeeCode(itemCode, InCityFeeCode) || IsFeeCode(itemCode, OutOfCityFeeCode)
           || NameStarts(description, InCityFeeName) || NameStarts(description, OutOfCityFeeName);

    private static bool IsFeeOf(string? itemCode, string? description, bool inCity)
        => inCity
            ? IsFeeCode(itemCode, InCityFeeCode) || NameStarts(description, InCityFeeName)
            : IsFeeCode(itemCode, OutOfCityFeeCode) || NameStarts(description, OutOfCityFeeName);

    /// <summary>Fiyat listesinden servis bedeli kalemini bulur (önce ada, sonra koda göre).</summary>
    public static PriceItemDto? FindFee(IEnumerable<PriceItemDto> priceItems, bool inCity)
    {
        var name = inCity ? InCityFeeName : OutOfCityFeeName;
        var code = inCity ? InCityFeeCode : OutOfCityFeeCode;
        var list = priceItems as IList<PriceItemDto> ?? priceItems.ToList();
        return list.FirstOrDefault(p => NameStarts(p.Description, name))
               ?? list.FirstOrDefault(p => IsFeeCode(p.PozNo, code));
    }

    /// <summary>
    /// Fiyat listesindeki ad "ad + tip" birleşimidir ("EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI"). Çıktıda MALZEME ADI ve
    /// MALZEME TİPİ ayrı sütunlardır: tip varsa ve ad onunla bitiyorsa sondan ayrılır; tip yoksa ad olduğu gibi (tekrar eden
    /// son kelimeler atılarak) döner ve tip BOŞ kalır.
    /// </summary>
    public static (string Name, string Type) SplitName(string? description, string? itemType)
    {
        var desc = string.Join(' ', (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var type = string.Join(' ', (itemType ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (type.Length == 0) return (CleanRepeatedName(desc), string.Empty);

        if (desc.Length > type.Length && desc.EndsWith(" " + type, true, Tr))
            return (desc[..^(type.Length + 1)].TrimEnd(), type);
        return string.Equals(desc, type, StringComparison.OrdinalIgnoreCase) ? (desc, string.Empty) : (desc, type);
    }

    /// <summary>
    /// Çıktıdaki MALZEME ADI: fiyat listesindeki adın tipsiz, kategori öneksiz hâli. Ekranda kalem "KATEGORİ - ad tip" biçiminde
    /// göründüğünden (fatura açıklaması) temiz ad, kalem fiyat listesinden eklenirken ayrıca saklanır (ExportDescription).
    /// </summary>
    public static string ExportName(string? priceListDescription, string? itemType) => SplitName(priceListDescription, itemType).Name;

    /// <summary>"X X" biçiminde iki kez yazılmış adı ("1 EKIP ... BEDELİ 1 EKIP ... BEDELİ") teke indirir.</summary>
    public static string CleanRepeatedName(string? description)
    {
        var tokens = (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2 && tokens.Length % 2 == 0)
        {
            var half = tokens.Length / 2;
            if (tokens.Take(half).SequenceEqual(tokens.Skip(half), StringComparer.OrdinalIgnoreCase))
                return string.Join(' ', tokens.Take(half));
        }
        return GlycolClaim.CleanItemName(string.Join(' ', tokens));
    }

    /// <summary>
    /// Formun kalem listesinde mağazanın iline uygun servis bedelinin bulunmasını sağlar: yoksa en başa ekler; il değiştiği için
    /// yanlış tür duruyorsa (şehir içi ↔ dışı) miktarı koruyarak değiştirir. İl bilinmiyorsa listeye dokunmaz.
    /// </summary>
    public static ServiceFeeResult ApplyServiceFee(List<ServiceItemDto> items, string? storeCity, PriceItemDto? inCityFee, PriceItemDto? outOfCityFee)
    {
        var inCity = IsInCity(storeCity);
        if (inCity is null) return ServiceFeeResult.UnknownCity;

        var price = inCity.Value ? inCityFee : outOfCityFee;
        if (price is null) return ServiceFeeResult.MissingPriceItem;

        if (items.Any(i => IsFeeOf(i.ItemCode, i.Description, inCity.Value))) return ServiceFeeResult.Unchanged;

        var other = items.FirstOrDefault(i => IsFeeOf(i.ItemCode, i.Description, !inCity.Value));
        if (other is not null)
        {
            Fill(other, price, inCity.Value);
            other.Recalculate();
            return ServiceFeeResult.Swapped;
        }

        var fee = new ServiceItemDto { LocalId = Guid.NewGuid(), Quantity = 1 };
        Fill(fee, price, inCity.Value);
        fee.Recalculate();
        items.Insert(0, fee);
        for (int i = 0; i < items.Count; i++) items[i].SortOrder = i + 1;
        return ServiceFeeResult.Added;
    }

    private static void Fill(ServiceItemDto item, PriceItemDto price, bool inCity)
    {
        item.Description = CleanRepeatedName(price.Description);
        item.ExportDescription = item.Description;
        item.ItemCode = string.IsNullOrWhiteSpace(price.PozNo) ? (inCity ? InCityFeeCode : OutOfCityFeeCode) : price.PozNo.Trim();
        item.ItemType = null;
        item.Unit = string.IsNullOrWhiteSpace(price.Unit) ? "set" : price.Unit;
        item.MaterialPrice = price.MaterialPrice;
        item.LaborPrice = price.LaborPrice;
        item.CurrencyCode = null;
        item.ForeignPrice = null;
        item.ExchangeRate = null;
        item.IsManualEntry = false;
    }
}
