namespace HakedisOtomasyon.Application.Analytics;

/// <summary>Yıl + ay çifti. Dönem aralığı filtrelerinde ve grafik eksenlerinde kullanılır.</summary>
public readonly record struct Period(int Year, int Month) : IComparable<Period>
{
    public int Index => Year * 12 + (Month - 1);
    public static Period FromIndex(int index) => new(index / 12, index % 12 + 1);
    public Period AddMonths(int months) => FromIndex(Index + months);
    public int CompareTo(Period other) => Index.CompareTo(other.Index);
    public static bool operator <=(Period a, Period b) => a.Index <= b.Index;
    public static bool operator >=(Period a, Period b) => a.Index >= b.Index;
    public static bool operator <(Period a, Period b) => a.Index < b.Index;
    public static bool operator >(Period a, Period b) => a.Index > b.Index;

    private static readonly string[] ShortMonths = { "Oca", "Şub", "Mar", "Nis", "May", "Haz", "Tem", "Ağu", "Eyl", "Eki", "Kas", "Ara" };
    public string Label => $"{ShortMonths[Month - 1]} {Year}";

    /// <summary>[from, to] aralığındaki tüm ayları sırayla döndürür.</summary>
    public static IEnumerable<Period> Range(Period from, Period to)
    {
        for (var i = from.Index; i <= to.Index; i++) yield return FromIndex(i);
    }
}

/// <summary>Analiz ekranlarının ortak filtresi.</summary>
public sealed record AnalyticsFilter(Period From, Period To, string? CompanyName = null, string? Region = null)
{
    public int MonthCount => To.Index - From.Index + 1;

    /// <summary>Aynı uzunluktaki bir önceki dönem (önceki döneme göre değişim yüzdesi için).</summary>
    public AnalyticsFilter Previous() => this with { From = From.AddMonths(-MonthCount), To = From.AddMonths(-1) };
}

// ── Ham satırlar (servis veritabanından okur, hesap bu satırlar üzerinden yapılır) ──────────

public sealed class ClaimFormRow
{
    public int ClaimId { get; init; }
    public Period Period { get; init; }
    public string CompanyName { get; init; } = string.Empty;
    public bool ClaimExported { get; init; }
    public string Category { get; init; } = string.Empty;
    public bool IsGlycolUsage { get; init; }
    public bool IsGasUsage { get; init; }
    public int FormId { get; init; }
    /// <summary>Yüklenen dosyanın yolu. Soğutmada tek PDF'ten birden fazla form kaydı türeyebilir; dosya sayısı bununla bulunur.</summary>
    public string FilePath { get; init; } = string.Empty;
    public bool IsCompleted { get; init; }
    public int? StoreId { get; init; }
    public string? StoreCode { get; init; }
    public string? StoreName { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public DateTime? FormDate { get; init; }
    public DateTime UploadedAt { get; init; }
    public List<ClaimItemRow> Items { get; init; } = new();
}

public sealed class ClaimItemRow
{
    public string Description { get; init; } = string.Empty;
    public string? ItemCode { get; init; }
    public string Unit { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal MaterialPrice { get; init; }
    public decimal LaborPrice { get; init; }
    public decimal TotalPrice { get; init; }
    public bool IsInvoiceBased { get; init; }
    public bool IsCurrencyBased { get; init; }
    public string? CurrencyCode { get; init; }
    public decimal? ExchangeRate { get; init; }
}

// ── Sonuç modelleri ────────────────────────────────────────────────────────────────

public sealed class ClaimKpis
{
    public int UploadedFiles { get; set; }
    public int FormCount { get; set; }
    public int CompletedForms { get; set; }
    public int IncompleteForms { get; set; }
    public int ClaimCount { get; set; }
    public int ActiveClaims { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PreviousTotalAmount { get; set; }
    /// <summary>Önceki eşit uzunluktaki döneme göre değişim (%). Önceki dönem 0 ise null.</summary>
    public decimal? ChangePercent { get; set; }
    public int RepeatFaultCount { get; set; }
    public int ConsumptionOutlierCount { get; set; }
}

public sealed record PeriodCategoryAmount(string PeriodLabel, string Category, decimal Amount);

public sealed record CostBreakdownRow(string PeriodLabel, decimal Material, decimal Labor, decimal Invoice, decimal Other)
{
    public decimal Total => Material + Labor + Invoice + Other;
}

public sealed record NamedAmount(string Name, decimal Amount);

public sealed record CurrencyPeriodRow(string PeriodLabel, string CurrencyCode, decimal ForeignTotal, decimal TlTotal, decimal AverageRate, decimal RateImpactTl);

public sealed record StoreCostRow(int StoreId, string StoreCode, string StoreName, string? City, int FormCount, decimal TotalAmount);

public sealed record RepeatFaultRow(string StoreCode, string StoreName, string Category, DateTime FirstDate, DateTime SecondDate, int DaysBetween);

public sealed record ConsumptionRow(string StoreCode, string StoreName, string? City, string Material, string Unit,
    int FormCount, decimal TotalQuantity, decimal PerForm, decimal Threshold, bool IsOutlier);

public sealed class ClaimAnalyticsResult
{
    public ClaimKpis Kpis { get; set; } = new();
    public List<string> PeriodLabels { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public List<PeriodCategoryAmount> MonthlyByCategory { get; set; } = new();
    public List<CostBreakdownRow> CostBreakdown { get; set; } = new();
    public List<NamedAmount> InvoiceShare { get; set; } = new();
    public List<CurrencyPeriodRow> Currency { get; set; } = new();
    public List<StoreCostRow> TopStores { get; set; } = new();
    public List<RepeatFaultRow> RepeatFaults { get; set; } = new();
    public List<ConsumptionRow> Consumption { get; set; } = new();
    public List<string> Companies { get; set; } = new();
    public List<string> Regions { get; set; } = new();
}
