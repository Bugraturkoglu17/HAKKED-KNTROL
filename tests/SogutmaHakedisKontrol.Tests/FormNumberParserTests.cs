using HakedisOtomasyon.Application;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Servis formunun sağ üstünden OCR ile okunan satırlardan form numarasının seçilmesi.</summary>
public class FormNumberParserTests
{
    [Fact]
    public void KirmiziDamgaYalnizBasina_NumaraSecilir()
        => Assert.Equal("20750", FormNumberParser.Pick(new[] { "20750" }));

    /// <summary>Siyah-beyaz taramada başlıktaki adres/telefon/e-posta satırları da okunur; onlar elenir.</summary>
    [Fact]
    public void AdresTelefonEpostaSatirlariElenir_AltindakiNumaraSecilir()
    {
        var lines = new[]
        {
            "2310/1 SOK. NO:86 GERSAN SANAYI SITESI",
            "ERGAZI - YENIMAHALLE / ANKARA",
            "Tel: 0.312.257 10 55 - 42 55 Fax: 0.312.257 07 05",
            "e-mail: teknikservis@intikos.com web: www.intikos.com",
            "20334",
            "SOĞUTMA MALZEME FORMU",
        };
        Assert.Equal("20334", FormNumberParser.Pick(lines));
    }

    [Theory]
    [InlineData("2 0 3 3 4", "20334")]   // OCR rakamları ayrı okuyabilir
    [InlineData("2O75O", "20750")]       // O harfi sıfır sanılmış
    [InlineData("8273", "8273")]         // 4 haneli form numaraları da var
    [InlineData("1234567", "1234567")]
    public void OcrKarisikliklariDuzeltilir(string line, string expected)
        => Assert.Equal(expected, FormNumberParser.Pick(new[] { line }));

    [Theory]
    [InlineData("0100")]                 // sıfırla başlayan (sayfa/kod parçası) alınmaz
    [InlineData("123")]                  // çok kısa
    [InlineData("12345678")]             // çok uzun (telefon vb.)
    [InlineData("20750 ADET")]           // sayı dışında metin varsa numara satırı değil
    [InlineData("NO: 20750")]
    [InlineData("")]
    public void NumaraOlmayanSatirlarAlinmaz(string line)
        => Assert.Null(FormNumberParser.Pick(new[] { line }));

    /// <summary>OKUR formları: numara "Servis No" yanında, sıfırla başlayan 6 hane — baştaki sıfırlar korunur.</summary>
    [Theory]
    [InlineData("005027")]
    [InlineData("004207")]
    [InlineData("0050271")]
    public void OkurFormu_SifirlaBaslayanAltiYediHane_Kabul(string line)
    {
        Assert.Equal(line, FormNumberParser.Pick(new[] { line }));
        Assert.Equal(line, FormNumberParser.Pick(new[] { line }, minDigits: 5));
    }

    [Theory]
    [InlineData("00502")]                // 5 hane, sıfırla başlıyor → kabul edilmez
    [InlineData("05027")]
    public void SifirlaBaslayanKisaNumaralarAlinmaz(string line) => Assert.Null(FormNumberParser.Pick(new[] { line }));

    /// <summary>Düz (kırmızı olmayan) okumada 4 haneli sayılar alınmaz: OKUR formundaki mavi Migros kaşesinin vergi no parçası
    /// ("9513") ya da elle yazılmış mağaza kodu ("6671") form numarası sanılmamalı.</summary>
    [Fact]
    public void DuzOkumada_KaseVeyaElYazisiDortHane_Alinmaz_FormNumarasiSecilir()
    {
        var okurHeader = new[] { "OKUR", "9513", "www okurteknikcontr", "004482", "SevsNb" };
        Assert.Equal("004482", FormNumberParser.Pick(okurHeader, minDigits: 5));
        Assert.Null(FormNumberParser.Pick(new[] { "9513", "6671" }, minDigits: 5));
        Assert.Equal("21177", FormNumberParser.Pick(new[] { "21177" }, minDigits: 5));   // INTİKOŞ siyah-beyaz tarama
        Assert.Equal("8273", FormNumberParser.Pick(new[] { "8273" }, minDigits: 4));     // INTİKOŞ kırmızı damga (4 hane)
    }

    [Fact]
    public void BirdenCokAdayVarsaEnUzunuSecilir()
        => Assert.Equal("21177", FormNumberParser.Pick(new[] { "7", "8273", "21177" }));

    [Fact]
    public void HicSatirYoksaNull()
        => Assert.Null(FormNumberParser.Pick(Array.Empty<string>()));
}
