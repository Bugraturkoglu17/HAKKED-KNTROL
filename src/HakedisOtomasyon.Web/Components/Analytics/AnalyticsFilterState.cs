namespace HakedisOtomasyon.Web.Components.Analytics;

/// <summary>Analiz ekranlarındaki filtre çubuğunun durumu (dönem aralığı, firma, bölge).</summary>
public sealed class AnalyticsFilterState
{
    public int FromYear { get; set; }
    public int FromMonth { get; set; }
    public int ToYear { get; set; }
    public int ToMonth { get; set; }
    public string? Company { get; set; }
    public string? Region { get; set; }

    public int FromIndex => FromYear * 12 + FromMonth - 1;
    public int ToIndex => ToYear * 12 + ToMonth - 1;

    /// <summary>Bitiş ayı verilen, geriye doğru <paramref name="months"/> ay süren aralık.</summary>
    public static AnalyticsFilterState LastMonths(int toYear, int toMonth, int months)
    {
        var to = toYear * 12 + toMonth - 1;
        var from = to - (months - 1);
        return new AnalyticsFilterState { FromYear = from / 12, FromMonth = from % 12 + 1, ToYear = toYear, ToMonth = toMonth };
    }

    public void SetRange(int fromIndex, int toIndex)
    {
        if (fromIndex > toIndex) (fromIndex, toIndex) = (toIndex, fromIndex);
        FromYear = fromIndex / 12; FromMonth = fromIndex % 12 + 1;
        ToYear = toIndex / 12; ToMonth = toIndex % 12 + 1;
    }

    /// <summary>Seçili tek ay (grafikten detaya inme).</summary>
    public void SetSingleMonth(int year, int month) => SetRange(year * 12 + month - 1, year * 12 + month - 1);
}
