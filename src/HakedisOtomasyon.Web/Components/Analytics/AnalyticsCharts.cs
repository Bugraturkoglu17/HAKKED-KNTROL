using System.Globalization;
using ApexCharts;

namespace HakedisOtomasyon.Web.Components.Analytics;

/// <summary>
/// Analiz ekranlarındaki grafiklerin ortak ApexCharts ayarları. Razor tarafında MudBlazor ile
/// isim çakışması (Color, Size, Align…) olmaması için seçenekler burada C# ile kurulur.
/// </summary>
public static class AnalyticsCharts
{
    /// <summary>Türkçe TL biçimi (tooltip/eksen). ApexCharts biçimlendiricileri JS fonksiyonu olarak verilir.</summary>
    private const string MoneyJs = "function (v) { if (v === null || v === undefined) return ''; return new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 }).format(v) + ' ₺'; }";
    private const string MoneyShortJs = "function (v) { if (v === null || v === undefined) return ''; var a = Math.abs(v); if (a >= 1e6) return (v / 1e6).toLocaleString('tr-TR', { maximumFractionDigits: 1 }) + ' Mn ₺'; if (a >= 1e3) return (v / 1e3).toLocaleString('tr-TR', { maximumFractionDigits: 0 }) + ' B ₺'; return v.toLocaleString('tr-TR', { maximumFractionDigits: 0 }) + ' ₺'; }";
    private const string NumberJs = "function (v) { if (v === null || v === undefined) return ''; return new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 2 }).format(v); }";
    private const string PercentJs = "function (v) { if (v === null || v === undefined) return ''; return v.toLocaleString('tr-TR', { maximumFractionDigits: 1 }) + ' %'; }";

    public static readonly string[] Palette =
    {
        "#1976D2", "#26A69A", "#FFA726", "#AB47BC", "#EF5350", "#8D6E63", "#5C6BC0", "#66BB6A", "#EC407A", "#78909C",
    };

    private static ApexChartOptions<T> Base<T>(bool dark) where T : class => new()
    {
        Chart = new Chart
        {
            Background = "transparent",
            FontFamily = "Roboto, Helvetica, Arial, sans-serif",
            Toolbar = new Toolbar { Show = false },
            Zoom = new Zoom { Enabled = false },
        },
        Theme = new Theme { Mode = dark ? Mode.Dark : Mode.Light },
        Colors = Palette.ToList(),
        DataLabels = new DataLabels { Enabled = false },
        Legend = new Legend { Position = LegendPosition.Bottom },
        NoData = new NoData { Text = "Bu dönemde veri yok" },
        Grid = new Grid { BorderColor = dark ? "#424242" : "#e0e0e0" },
    };

    /// <summary>TL değerli çubuk/çizgi grafik. stacked: üst üste yığılmış; horizontal: yatay çubuk.</summary>
    public static ApexChartOptions<T> Money<T>(bool dark, bool stacked = false, bool horizontal = false) where T : class
    {
        var o = Base<T>(dark);
        o.Chart.Stacked = stacked;
        o.PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = horizontal, BorderRadius = 3, ColumnWidth = "60%" } };
        o.Tooltip = new Tooltip { Shared = !horizontal, Intersect = false, Y = new TooltipY { Formatter = MoneyJs } };
        if (horizontal)
            o.Xaxis = new XAxis { Labels = new XAxisLabels { Formatter = MoneyShortJs } };
        else
            o.Yaxis = new List<YAxis> { new() { Labels = new YAxisLabels { Formatter = MoneyShortJs } } };
        return o;
    }

    /// <summary>Miktar (adet/kg) çubuk grafiği.</summary>
    public static ApexChartOptions<T> Quantity<T>(bool dark, bool horizontal = false) where T : class
    {
        var o = Base<T>(dark);
        o.PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = horizontal, BorderRadius = 3 } };
        o.Tooltip = new Tooltip { Y = new TooltipY { Formatter = NumberJs } };
        return o;
    }

    /// <summary>Yüzde değerli yatay çubuk.</summary>
    public static ApexChartOptions<T> Percent<T>(bool dark, bool horizontal = true) where T : class
    {
        var o = Base<T>(dark);
        o.PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = horizontal, BorderRadius = 3 } };
        o.Tooltip = new Tooltip { Y = new TooltipY { Formatter = PercentJs } };
        if (horizontal) o.Xaxis = new XAxis { Labels = new XAxisLabels { Formatter = PercentJs } };
        else o.Yaxis = new List<YAxis> { new() { Labels = new YAxisLabels { Formatter = PercentJs } } };
        return o;
    }

    /// <summary>Halka grafik. money=true ise dilimler TL, değilse adet.</summary>
    public static ApexChartOptions<T> Donut<T>(bool dark, IEnumerable<string>? colors = null, bool money = false) where T : class
    {
        var o = Base<T>(dark);
        if (colors is not null) o.Colors = colors.ToList();
        o.DataLabels = new DataLabels { Enabled = true };
        o.Tooltip = new Tooltip { Y = new TooltipY { Formatter = money ? MoneyJs : NumberJs } };
        o.PlotOptions = new PlotOptions { Pie = new PlotOptionsPie { Donut = new PlotOptionsDonut { Size = "60%" } } };
        return o;
    }

    public static string Tl(decimal v) => v.ToString("N0", Tr) + " ₺";
    public static string Num(decimal v, int decimals = 0) => v.ToString("N" + decimals, Tr);
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
}
