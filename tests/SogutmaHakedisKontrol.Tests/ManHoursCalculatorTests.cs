using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Spec §5 / §21 Test 1 ve Test 2 — adam-saat matematiği AI'da değil, burada yapılır.</summary>
public class ManHoursCalculatorTests
{
    private readonly ManHoursCalculator _calc = new();

    [Fact]
    public void Test1_IkiKisiIkiSaat_ToplamDortAdamSaat_OdenebilirSifir()
    {
        var h1 = _calc.CalculateHours(TimeSpan.Parse("10:00"), TimeSpan.Parse("12:00"));
        var h2 = _calc.CalculateHours(TimeSpan.Parse("10:00"), TimeSpan.Parse("12:00"));
        var total = h1!.Value + h2!.Value;

        Assert.Equal(4m, total);
        Assert.Equal(0m, _calc.CalculatePayableHours(total));
    }

    [Fact]
    public void Test2_UcKisiBeserSaat_OnBesAdamSaat_OdenebilirOnBir()
    {
        var total = Enumerable.Range(0, 3)
            .Select(_ => _calc.CalculateHours(TimeSpan.Parse("10:00"), TimeSpan.Parse("15:00"))!.Value)
            .Sum();

        Assert.Equal(15m, total);
        Assert.Equal(11m, _calc.CalculatePayableHours(total));
    }

    [Fact]
    public void UcKisiDortSaat_OnIkiAdamSaat_OdenebilirSekiz()
    {
        var total = Enumerable.Range(0, 3)
            .Select(_ => _calc.CalculateHours(TimeSpan.Parse("10:00"), TimeSpan.Parse("14:00"))!.Value)
            .Sum();

        Assert.Equal(12m, total);
        Assert.Equal(8m, _calc.CalculatePayableHours(total));
    }

    [Fact]
    public void EksikSaat_NullDoner_UydurulmazHesaplanmaz()
    {
        Assert.Null(_calc.CalculateHours(null, TimeSpan.Parse("12:00")));
        Assert.Null(_calc.CalculateHours(TimeSpan.Parse("10:00"), null));
    }

    [Fact]
    public void GeceYarisiniGecenVardiya_DogruHesaplanir()
    {
        // 22:00 - 02:00 => 4 saat
        var hours = _calc.CalculateHours(TimeSpan.Parse("22:00"), TimeSpan.Parse("02:00"));
        Assert.Equal(4m, hours);
    }
}
