using HakedisOtomasyon.Application;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>TL tutarının "29.682,53" biçiminde yazılıp okunması (makinenin bölge ayarından bağımsız).</summary>
public class TrMoneyTests
{
    [Theory]
    [InlineData(29682.53, "29.682,53")]
    [InlineData(29682, "29.682,00")]
    [InlineData(111407.483, "111.407,48")]
    [InlineData(1234567.5, "1.234.567,50")]
    [InlineData(750, "750,00")]
    [InlineData(0.5, "0,50")]
    public void Format_BinlikAyiriciVeIkiOndalik(double amount, string expected)
        => Assert.Equal(expected, TrMoney.Format((decimal)amount));

    [Theory]
    [InlineData("29682,53", 29682.53)]        // kullanıcının yazdığı
    [InlineData("29.682,53", 29682.53)]       // alanın gösterdiği biçim (yeniden okunabilmeli)
    [InlineData("29682.53", 29682.53)]        // sayısal tuş takımındaki nokta
    [InlineData("29.682", 29682)]             // nokta + tam 3 rakam → binlik ayırıcı
    [InlineData("29682", 29682)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("1.234.567,5", 1234567.5)]
    [InlineData(" 29 682,53 ₺ ", 29682.53)]
    [InlineData("29.682,53 TL", 29682.53)]
    [InlineData("0,5", 0.5)]
    [InlineData("12.5", 12.5)]                // nokta + 1 rakam → ondalık
    [InlineData("12.50", 12.5)]
    public void TryParse_YazilanTutariCozer(string text, double expected)
    {
        Assert.True(TrMoney.TryParse(text, out var amount));
        Assert.Equal((decimal)expected, amount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12,34,56")]
    [InlineData("1,234.56")]                  // İngilizce biçim kabul edilmez (yanlış okunmasın)
    [InlineData("-5")]
    public void TryParse_GecersizMetniReddeder(string? text) => Assert.False(TrMoney.TryParse(text, out _));

    [Theory]
    [InlineData(29682.53)]
    [InlineData(111407.48)]
    [InlineData(1000)]
    [InlineData(0.07)]
    public void YazVeOku_AyniDegeriVerir(double amount)
    {
        Assert.True(TrMoney.TryParse(TrMoney.Format((decimal)amount), out var back));
        Assert.Equal((decimal)amount, back);
    }
}
