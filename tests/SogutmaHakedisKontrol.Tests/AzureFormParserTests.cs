using HakedisOtomasyon.Application;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Azure'un servis formundan okuduğu satırlardan mağaza ve tarih çıkarma (gerçek bir INTİKOŞ formunun Azure çıktısı).</summary>
public class AzureFormParserTests
{
    private static List<OcrLine> IntikosForm() => new()
    {
        new("İNTİKON R İNŞAAT VE TİCARET KOLL. ŞTİ.", 0.25, 0.04),
        new("21174", 0.85, 0.10),                                       // sağ üstteki basılı form numarası
        new("SOĞUTMA MALZEME FORMU", 0.50, 0.13),
        new("ÇAĞRI BILDIREN: Engana evleri Mim migros (6955)", 0.40, 0.16),
        new("İŞİN YERİ:", 0.10, 0.17),
        new("Tarih", 0.45, 0.52),
        new("23.072", 0.45, 0.54),
        new("SERVIS ELEMANI (İSİM, İMZA, KAŞE) Eres Toma ERGÜN", 0.20, 0.94),
        new("TARİH 23/07/2026", 0.50, 0.95),
    };

    [Fact]
    public void ParantezliKod_VeAd_CagriBildirenSatirindan()
    {
        var (code, name) = AzureFormParser.Store(IntikosForm());
        Assert.Equal("6955", code);
        Assert.Equal("Engana evleri Mim migros", name);
    }

    [Fact]
    public void Tarih_AlttakiTarihSatirindan()
        => Assert.Equal((23, 7), AzureFormParser.Date(IntikosForm()));

    [Fact]
    public void ParantezsizKod_UsteElleYazilmisSayi_FormNumarasiAlinmaz()
    {
        var lines = new List<OcrLine>
        {
            new("2890", 0.50, 0.09),        // elle yazılmış mağaza kodu (ortada)
            new("20429", 0.86, 0.11),       // basılı form numarası (sağda) → alınmaz
            new("SOĞUTMA MALZEME FORMU", 0.50, 0.14),
            new("ÇAĞRI BİLDİREN : 5M AFYON", 0.30, 0.16),
        };
        Assert.Equal(("2890", "5M AFYON"), AzureFormParser.Store(lines));
    }

    [Fact]
    public void AltTarihYoksa_PersonelTablosundakiTarih()
    {
        var lines = new List<OcrLine> { new("Ali Karacan 02/07/26 14:00", 0.3, 0.55), new("TARİH ......./......./202..", 0.5, 0.95) };
        Assert.Equal((2, 7), AzureFormParser.Date(lines));
    }

    /// <summary>Gerçek Azure çıktısı: etiket ve değer AYRI satır, aynı hizada; "MAĞAZA ONAY" mağaza sayılmaz.</summary>
    [Fact]
    public void EtiketVeDegerAyriSatir_AyniHizada()
    {
        var afyon = new List<OcrLine>
        {
            new("2890", 0.51, 0.13), new("20429", 0.86, 0.15), new("SOĞUTMA MALZEME FORMU", 0.49, 0.18),
            new("ÇAĞRI BİLDİREN :", 0.12, 0.20), new("5M AFYON", 0.28, 0.20),
            new("İŞİN YERİ:", 0.10, 0.22), new("MIGROS TICARET A.S.", 0.26, 0.22),
            new("Tarih", 0.37, 0.52),
            new("TARİH", 0.40, 0.93), new("04,07", 0.46, 0.93), new("202 6", 0.53, 0.94),
            new("IKbal Outlet Ce MAĞAZA ONAY", 0.68, 0.92),
        };
        Assert.Equal(("2890", "5M AFYON"), AzureFormParser.Store(afyon));
        Assert.Equal((4, 7), AzureFormParser.Date(afyon));

        var acil = new List<OcrLine>
        {
            new("4321", 0.49, 0.12), new("25684", 0.83, 0.16), new("ACİL ÇAĞRI FORMU", 0.48, 0.20),
            new("ACİL ÇAĞRI BİLDİREN :", 0.14, 0.22), new("İŞİN YERİ: BINEVLER M MIGROS CORUM", 0.32, 0.23),
            new("TARİH 03/09/2026", 0.47, 0.93), new("MAĞAZA ONAY", 0.73, 0.93),
        };
        Assert.Equal(("4321", "BINEVLER M MIGROS CORUM"), AzureFormParser.Store(acil));
        Assert.Equal((3, 9), AzureFormParser.Date(acil));
    }

    /// <summary>Kullanıcı örneği: ad okunamıyor ("İZ FMS"), üstte el yazısı "4280" var, "İŞİN YERİ" satırında da kod geçiyor.</summary>
    [Theory]
    [InlineData("4280")]
    [InlineData("4 280")]
    [InlineData("(4280)")]
    [InlineData("4.280")]
    public void AdOkunamasa_UstteElYazisiKodAlinir(string written)
    {
        var lines = new List<OcrLine>
        {
            new("ISO 9001:2015", 0.45, 0.38), new(written, 0.52, 0.34), new("014535", 0.80, 0.40),
            new("SOĞUTMA MALZEME FORMU", 0.50, 0.55),
            new("ÇAĞRI BİLDİREN :", 0.12, 0.62), new("İZ FMS", 0.28, 0.62),
            new("İŞİN YERİ:", 0.10, 0.66), new("Ergazi Gençlik M Jet Migros 4280", 0.40, 0.66),
        };
        Assert.Equal("4280", AzureFormParser.Store(lines).Code);
        Assert.Equal("4280", AzureFormParser.StoreCodes(lines)[0]);
    }

    [Fact]
    public void UstKodYoksa_IsinYeriSatirindakiKodAday_OlurVeSiraKorunur()
    {
        var lines = new List<OcrLine>
        {
            new("SOĞUTMA MALZEME FORMU", 0.50, 0.55),
            new("ÇAĞRI BİLDİREN :", 0.12, 0.62), new("İZ FMS", 0.28, 0.62),
            new("İŞİN YERİ:", 0.10, 0.66), new("Ergazi Gençlik M Jet Migros 4280", 0.40, 0.66),
        };
        Assert.Equal(new[] { "4280" }, AzureFormParser.StoreCodes(lines));
        Assert.Null(AzureFormParser.Store(lines).Code);   // ad etiketlerinden kod çıkmaz; yalnız aday listesinde
    }

    /// <summary>Gerçek Azure çıktısı (TEMMUZ glikol formları): "Glikol" bozuk okunur, miktar adda "80kg" ya da sağda "4bidon" yazar.</summary>
    [Fact]
    public void Glikol_AdHucresindeKg_BidonlaTutarliysaTeyitsiz()
    {
        // gerçek güvenler: "Glika" 0.67 (bozuk ad), "80kg" 0.95, "4bidon" 0.62 → hücre güveni 0.67 olsa da miktar net
        var name = new OcrCell(1, 0, "1 Glika 80kg", 0.67) { Words = new[] { new OcrWord("1", 1), new OcrWord("Glika", 0.67), new OcrWord("80kg", 0.95) } };
        var g = AzureFormParser.Glycol(new[] { WasteTable(), MaterialTable(name, new(1, 1, "4bidon", 0.62), new(1, 2, "11", 1)) })!;
        Assert.Equal(80m, g.Quantity);
        Assert.False(g.NeedsCheck);
    }

    [Fact]
    public void Glikol_MiktarKelimesiDusukGuvenliyse_TeyitIster()
    {
        var name = new OcrCell(1, 0, "1 Glikol 80kg", 0.5) { Words = new[] { new OcrWord("Glikol", 0.9), new OcrWord("80kg", 0.5) } };
        Assert.True(AzureFormParser.Glycol(new[] { MaterialTable(name, new(1, 1, "", 1)) })!.NeedsCheck);
    }

    [Fact]
    public void Glikol_YalnizBidonYaziyorsa_Bidon20KgVeTeyitIster()
    {
        var g = AzureFormParser.Glycol(new[] { MaterialTable(new(1, 0, "1 Rhol AGa", 0.9), new(1, 1, "Visp", 0.9), new(4, 0, "4 Gilla 404", 0.9), new(4, 1, "2bdes", 0.9)) })!;
        Assert.Equal(40m, g.Quantity);
        Assert.True(g.NeedsCheck);
    }

    [Fact]
    public void Glikol_SagdakiMiktarKgIse_Okunur()
    {
        var g = AzureFormParser.Glycol(new[] { MaterialTable(new(1, 0, "1 Glikol", 1), new(1, 1, "40 kg", 0.95)) })!;
        Assert.Equal(40m, g.Quantity);
        Assert.False(g.NeedsCheck);
    }

    [Fact]
    public void Glikol_BidonVeKgTutmuyorsa_TeyitIster()
    {
        var g = AzureFormParser.Glycol(new[] { MaterialTable(new(1, 0, "1 Glikol 80kg", 1), new(1, 1, "3 bidon", 1)) })!;
        Assert.True(g.NeedsCheck);
    }

    [Fact]
    public void Glikol_DusukGuven_TeyitIster()
        => Assert.True(AzureFormParser.Glycol(new[] { MaterialTable(new(1, 0, "1 Glikol 80kg", 0.5), new(1, 1, "", 1)) })!.NeedsCheck);

    [Fact]
    public void Glikol_GazSatiriGlikolSayilmaz_GlikolYoksaNull()
    {
        var table = MaterialTable(new(1, 0, "1 R404 gaz", 1), new(1, 1, "7kg", 1), new(2, 0, "2 Hava borusu", 1), new(2, 1, "2 Adet", 1));
        Assert.Null(AzureFormParser.Glycol(new[] { table }));
    }

    [Fact]
    public void Glikol_AtikTablosuSayilmaz()
        => Assert.Null(AzureFormParser.Glycol(new[] { new OcrTable(new List<OcrCell> { new(0, 0, "ATIK Malzemenin Cinsi", 1), new(0, 1, "Miktarı", 1), new(1, 0, "1 Glikol", 1), new(1, 1, "20kg", 1) }) }));

    [Fact]
    public void GazSatiri_GazOkuyucuyaDokunulmadi()
        => Assert.Equal(7m, AzureFormParser.Gas(new[] { MaterialTable(new(1, 0, "1 R404 gaz", 1), new(1, 1, "7kg", 1)) })!.Quantity);

    // ── Gaz miktarı ("Kullanılan Malzeme" tablosu; gerçek gaz formlarının Azure çıktısı) ──
    private static OcrTable MaterialTable(params OcrCell[] rows) => new(new List<OcrCell>
    {
        new(0, 0, "Malzemenin Cinsi", 1), new(0, 1, "Miktarı", 1), new(0, 2, "Malzemenin Cinsi", 1), new(0, 3, "Miktarı", 1),
    }.Concat(rows).ToList());

    private static OcrTable WasteTable() => new(new List<OcrCell>
    {
        new(0, 0, "ATIK Malzemenin Cinsi", 1), new(0, 1, "Miktarı", 1), new(1, 0, "1 R404 gaz", 1), new(1, 1, "3kg", 1),
    });

    [Theory]
    [InlineData("IR-404 A Gaz", "20kg", 20)]
    [InlineData("1 R-404 A Gaz", "25kg", 25)]
    [InlineData("gaz", "5,5 kg", 5.5)]
    [InlineData("R22", "12", 12)]
    public void GazSatiri_MiktarOkunur_Teyitsiz(string name, string qty, decimal expected)
    {
        var g = AzureFormParser.Gas(new[] { WasteTable(), MaterialTable(new(1, 0, name, 0.95), new(1, 1, qty, 0.95), new(1, 2, "11", 1)) })!;
        Assert.Equal(expected, g.Quantity);
        Assert.False(g.NeedsCheck);
    }

    [Fact]
    public void GazTuru_Okunur()
        => Assert.Equal("R404", AzureFormParser.Gas(new[] { MaterialTable(new(1, 0, "IR-404 A Gaz", 1), new(1, 1, "20kg", 1)) })!.GasType);

    [Fact]
    public void SagYaridakiGaz_SagdakiMiktarSutunundan()
    {
        var g = AzureFormParser.Gas(new[] { MaterialTable(new(1, 0, "1 Expansion", 1), new(1, 1, "2ad", 1), new(1, 2, "11 R404 gaz", 1), new(1, 3, "7kg", 1)) })!;
        Assert.Equal(7m, g.Quantity);
    }

    [Theory]
    [InlineData("2Okg", 0.95)]      // harf karışmış
    [InlineData("20kg", 0.40)]      // düşük güven
    [InlineData("", 1)]             // miktar yazılmamış
    public void BozukMiktar_TeyitIster(string qty, double conf)
    {
        var g = AzureFormParser.Gas(new[] { MaterialTable(new(1, 0, "R404 Gaz", 1), new(1, 1, qty, conf)) })!;
        Assert.True(g.NeedsCheck);
        Assert.NotNull(g.Note);
    }

    [Fact]
    public void IkiGazSatiri_ToplanirTeyitIster()
    {
        var g = AzureFormParser.Gas(new[] { MaterialTable(new(1, 0, "R404 Gaz", 1), new(1, 1, "10kg", 1), new(2, 0, "R404 gaz", 1), new(2, 1, "5kg", 1)) })!;
        Assert.Equal(15m, g.Quantity);
        Assert.True(g.NeedsCheck);
    }

    [Fact]
    public void GazSatiriYoksa_Null_AtikTablosuSayilmaz()
        => Assert.Null(AzureFormParser.Gas(new[] { WasteTable(), MaterialTable(new(1, 0, "Expansion", 1), new(1, 1, "2 ad", 1)) }));

    /// <summary>Kod parantezsiz, satırın sonunda: "365 Zirve Kant 3M Minros 1996" → 1996 (baştaki 365 değil).</summary>
    [Fact]
    public void ParantezsizKod_SatirSonundakiSayi()
    {
        var lines = new List<OcrLine> { new("SOĞUTMA MALZEME FORMU", 0.5, 0.13), new("ÇAĞRI BİLDİREN : 365 Zirve Kant 3M Minros 1996", 0.4, 0.16) };
        Assert.Equal(("1996", "365 Zirve Kant 3M Minros"), AzureFormParser.Store(lines));
    }

    [Theory]
    [InlineData("R-4O4 A 6az")]
    [InlineData("R-40h A Gaz")]
    public void BozukOkunanGazYazisi_Taninir(string name)
    {
        var g = AzureFormParser.Gas(new[] { MaterialTable(new(1, 0, name, 1), new(1, 1, "5kg", 1)) })!;
        Assert.Equal(5m, g.Quantity);
        Assert.Equal("R404", g.GasType);
    }

    [Fact]
    public void GazYazisiTaninmazsa_TekDoluSatirAlinir_TeyitIster()
    {
        var g = AzureFormParser.Gas(new[] { MaterialTable(new(1, 0, "1 R-hOh A 6@", 1), new(1, 1, "5kg", 1)) })!;
        Assert.Equal(5m, g.Quantity);
        Assert.True(g.NeedsCheck);
    }

    [Fact]
    public void BilgiYoksa_Bos()
    {
        Assert.Equal((null, null), AzureFormParser.Store(new List<OcrLine> { new("SOĞUTMA MALZEME FORMU", 0.5, 0.1) }));
        Assert.Equal((null, null), AzureFormParser.Date(new List<OcrLine>()));
    }
}
