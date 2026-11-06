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

    [Fact]
    public void BilgiYoksa_Bos()
    {
        Assert.Equal((null, null), AzureFormParser.Store(new List<OcrLine> { new("SOĞUTMA MALZEME FORMU", 0.5, 0.1) }));
        Assert.Equal((null, null), AzureFormParser.Date(new List<OcrLine>()));
    }
}
