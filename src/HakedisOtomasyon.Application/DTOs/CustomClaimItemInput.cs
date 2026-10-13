namespace HakedisOtomasyon.Application.DTOs;

/// <summary>Hakedişe özel (fiyat listesinde olmayan) kalemin fiyatının neye dayandığı — çıktıda MALZEME KODU sütununa yazılır.</summary>
public enum CustomPriceBasis
{
    /// <summary>Üretici/satıcı liste fiyatı → "LİSTE FİYATI".</summary>
    ListPrice = 0,
    /// <summary>İskontolu liste fiyatı: liste fiyatı × (1 − iskonto) → "%10 İSKONTOLU LİSTE FİYATI".</summary>
    DiscountedListPrice = 1,
    /// <summary>Fatura tutarı + ilave: fatura × (1 + ilave) → "FATURA +%10 M.K.". Fatura dosyası eklenir.</summary>
    InvoicePlusMarkup = 2,
}

/// <summary>
/// Soğutma Kompresör Değişim hakedişinde YALNIZCA o hakedişe eklenen özel kalem (kompresör marka/modeli vb.).
/// Genel fiyat listesine EKLENMEZ — kalıcı malzeme yalnızca Fiyat Listesi sayfasından girilir.
/// </summary>
public sealed class CustomClaimItemInput
{
    /// <summary>MALZEME ADI — örn. "BİTZER 4J-13-2Y-40P VE 4NCS-12.2Y-40P".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>MALZEME KODU — isteğe bağlı (örn. "ELK.MLZ. 3.6"). Boşsa fiyat türüne göre etiket yazılır
    /// ("FATURA +%10 M.K.", "LİSTE FİYATI", "%10 İSKONTOLU LİSTE FİYATI").</summary>
    public string? Code { get; set; }

    /// <summary>MALZEME TİPİ — örn. "SIFIR KOMPRESÖR", "2 ADET KOMPRESÖR REVİZYONU"; boş olabilir.</summary>
    public string? Type { get; set; }

    public decimal Quantity { get; set; } = 1m;
    public string Unit { get; set; } = "ADET";

    public CustomPriceBasis Basis { get; set; } = CustomPriceBasis.InvoicePlusMarkup;

    /// <summary>Temel tutar (TL, birim başına): liste fiyatı ya da fatura tutarı.</summary>
    public decimal BaseAmount { get; set; }

    /// <summary>Yüzde: iskonto oranı (iskontolu liste fiyatı) ya da ilave oranı (fatura). Elle girilir; liste fiyatında kullanılmaz.</summary>
    public decimal Percent { get; set; } = 10m;

    /// <summary>Fatura dosyasının uygulama deposundaki göreli yolu (yalnız fatura bazlı kalemde; boş olabilir).</summary>
    public string? InvoiceFilePath { get; set; }
    public string? InvoiceFileName { get; set; }
}
