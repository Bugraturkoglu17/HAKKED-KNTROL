using HakedisOtomasyon.Application;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Servis formu ekranındaki ileri/geri oklar: çok sayfalı PDF'de önce sayfalar, sonra sonraki dosya.</summary>
public class FilePageCursorTests
{
    private static readonly string[] Files = { "a.pdf", "b.pdf", "c.png" };

    private static FilePageCursor Cursor()
    {
        var c = new FilePageCursor();
        c.SetPageCount("a.pdf", 1);
        c.SetPageCount("b.pdf", 3);
        c.SetPageCount("c.png", 1);
        return c;
    }

    [Fact]
    public void Ileri_CokSayfaliPdfdeSayfaSayfaGezer_SonraSonrakiDosya()
    {
        var c = Cursor();
        var seen = new List<(int, int)> { (c.FileIndex, c.PageIndex) };
        while (c.Next(Files)) seen.Add((c.FileIndex, c.PageIndex));
        Assert.Equal(new[] { (0, 0), (1, 0), (1, 1), (1, 2), (2, 0) }, seen);
        Assert.False(c.CanNext(Files));
    }

    [Fact]
    public void Geri_OncekiDosyaninSonSayfasinaGider()
    {
        var c = Cursor();
        c.GoToFile(2);
        Assert.True(c.Previous(Files));
        Assert.Equal((1, 2), (c.FileIndex, c.PageIndex));
        c.Previous(Files); c.Previous(Files); c.Previous(Files);
        Assert.Equal((0, 0), (c.FileIndex, c.PageIndex));
        Assert.False(c.CanPrevious);
        Assert.False(c.Previous(Files));
    }

    [Fact]
    public void SayfaSayisiBilinmeyenDosya_TekSayfaSayilir_SilinenDosyadaKonumDuzelir()
    {
        var c = new FilePageCursor();
        Assert.Equal(1, c.PagesOf("x.pdf"));
        c.SetPageCount("x.pdf", 0);              // okunamayan dosya
        Assert.Equal(1, c.PagesOf("x.pdf"));

        var k = Cursor();
        k.GoToFile(1); k.Next(Files); k.Next(Files);   // b.pdf 3. sayfa
        k.Clamp(new[] { "a.pdf" });                     // b ve c silindi
        Assert.Equal((0, 0), (k.FileIndex, k.PageIndex));
    }
}
