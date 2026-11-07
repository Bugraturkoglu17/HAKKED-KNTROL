using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Application.Analytics;

/// <summary>Kontrol analizi filtresi. Dönemler yıl*12 + (ay-1) indeksiyle karşılaştırılır.</summary>
public sealed record ControlAnalyticsFilter(int FromYear, int FromMonth, int ToYear, int ToMonth, string? CompanyName = null, string? Region = null)
{
    public int FromIndex => FromYear * 12 + FromMonth - 1;
    public int ToIndex => ToYear * 12 + ToMonth - 1;
}

// ── Ham satırlar ────────────────────────────────────────────────────────────────

public sealed class ControlCheckRow
{
    public int Id { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public string CompanyName { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public string PeriodLabel { get; init; } = string.Empty;
    public bool IsCompleted { get; init; }
    public decimal CompanyTotal { get; init; }
    public decimal CalculatedTotal { get; init; }
    public decimal Difference { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<ControlItemRow> Items { get; init; } = new();
    public List<ControlAiRow> AiResults { get; init; } = new();
    public int PeriodIndex => Year * 12 + Month - 1;
}

public sealed class ControlItemRow
{
    public string MaterialName { get; init; } = string.Empty;
    public string? StoreCity { get; init; }
    public CheckItemControlStatus Status { get; init; }
    public decimal CompanyLineTotal { get; init; }
    public decimal? Difference { get; init; }
    public decimal? DifferencePercent { get; init; }
    public bool IsExcluded { get; init; }
}

public sealed class ControlAiRow
{
    public AiComparisonStatus Status { get; init; }
    public AiComparisonItemType ItemType { get; init; }
    public bool UserOverridden { get; init; }
}

// ── Sonuç modelleri ─────────────────────────────────────────────────────────────

public sealed class ControlKpis
{
    public int CheckCount { get; set; }
    public int CompletedChecks { get; set; }
    public decimal CompanyTotal { get; set; }
    public decimal CalculatedTotal { get; set; }
    /// <summary>Firma talebi − hesaplanan. Pozitif: firmanın fazla talep ettiği tutar.</summary>
    public decimal Difference { get; set; }
    public decimal OverbilledTotal { get; set; }
    public int ItemCount { get; set; }
    public int ErrorItemCount { get; set; }
    public int PendingItemCount { get; set; }
    public decimal ErrorRatePercent { get; set; }
}

public sealed record ControlPeriodRow(int Year, int Month, string PeriodLabel, decimal CompanyTotal, decimal CalculatedTotal, decimal Difference);
public sealed record ControlStatusRow(string Status, int Count, decimal Difference, string Color);
public sealed record ControlMaterialRow(string MaterialName, int Count, decimal TotalDifference, decimal AverageDifferencePercent);
public sealed record ControlCityRow(string City, int ItemCount, int ErrorCount, decimal ErrorRatePercent, decimal TotalDifference);
public sealed record ControlCompanyRow(string CompanyName, int CheckCount, int ItemCount, decimal ErrorRatePercent, decimal AverageDifferencePercent, decimal TotalDifference);
public sealed record ControlAiStatusRow(string Status, int Count, string Color);
public sealed record ControlAiTypeRow(string ItemType, int Total, int Uygun, int UygunDegil, int Eksik, int Manuel, int Overridden);
public sealed record ControlCheckListRow(int Id, string PeriodLabel, string CompanyName, string Region, string CategoryName,
    decimal CompanyTotal, decimal CalculatedTotal, decimal Difference, int ErrorItemCount, bool IsCompleted);

public sealed class ControlAnalyticsResult
{
    public ControlKpis Kpis { get; set; } = new();
    public List<ControlPeriodRow> Periods { get; set; } = new();
    public List<ControlStatusRow> StatusDistribution { get; set; } = new();
    public List<ControlMaterialRow> TopMaterials { get; set; } = new();
    public List<ControlCityRow> Cities { get; set; } = new();
    public List<ControlCompanyRow> Companies { get; set; } = new();
    public List<ControlAiStatusRow> AiStatuses { get; set; } = new();
    public List<ControlAiTypeRow> AiByType { get; set; } = new();
    public int AiTotal { get; set; }
    public int AiOverridden { get; set; }
    public decimal AiOverrideRatePercent { get; set; }
    public List<ControlCheckListRow> Checks { get; set; } = new();
    public List<string> CompanyOptions { get; set; } = new();
    public List<string> RegionOptions { get; set; } = new();
}

/// <summary>Hakediş kontrol sonuçlarından grafik verisi üretir (veritabanından bağımsız).</summary>
public static class ControlAnalyticsCalculator
{
    public static readonly CheckItemControlStatus[] ErrorStatuses =
    {
        CheckItemControlStatus.FiyatHatasi, CheckItemControlStatus.BirimFiyatBulunamadi, CheckItemControlStatus.BirimUyusmazligi,
    };

    public static bool IsError(CheckItemControlStatus s) => Array.IndexOf(ErrorStatuses, s) >= 0;
    public static bool IsPending(CheckItemControlStatus s) => s is CheckItemControlStatus.KontrolGerekli or CheckItemControlStatus.OnayBekliyor;

    public static string StatusLabel(CheckItemControlStatus s) => s switch
    {
        CheckItemControlStatus.Uygun => "Uygun",
        CheckItemControlStatus.FiyatHatasi => "Fiyat Hatası",
        CheckItemControlStatus.BirimFiyatBulunamadi => "Fiyatı Bulunamadı",
        CheckItemControlStatus.BirimUyusmazligi => "Birim Uyuşmazlığı",
        CheckItemControlStatus.OnayBekliyor => "Onay Bekliyor",
        CheckItemControlStatus.KontrolGerekli => "Kontrol Gerekli",
        CheckItemControlStatus.KontrolDisi => "Kontrol Dışı",
        _ => s.ToString(),
    };

    public static string StatusColor(CheckItemControlStatus s) => s switch
    {
        CheckItemControlStatus.Uygun => "#2e7d32",
        CheckItemControlStatus.FiyatHatasi => "#c62828",
        CheckItemControlStatus.BirimFiyatBulunamadi => "#ad1457",
        CheckItemControlStatus.BirimUyusmazligi => "#ef6c00",
        CheckItemControlStatus.OnayBekliyor => "#f9a825",
        CheckItemControlStatus.KontrolGerekli => "#fbc02d",
        _ => "#9e9e9e",
    };

    public static string AiStatusLabel(AiComparisonStatus s) => s switch
    {
        AiComparisonStatus.Uygun => "Uygun",
        AiComparisonStatus.UygunDegil => "Uygun Değil",
        AiComparisonStatus.Eksik => "Eksik",
        AiComparisonStatus.ManuelKontrol => "Manuel Kontrol",
        _ => "Diğer",
    };

    private static string AiStatusColor(AiComparisonStatus s) => s switch
    {
        AiComparisonStatus.Uygun => "#2e7d32",
        AiComparisonStatus.UygunDegil => "#c62828",
        AiComparisonStatus.Eksik => "#ef6c00",
        AiComparisonStatus.ManuelKontrol => "#1565c0",
        _ => "#9e9e9e",
    };

    public static string AiItemTypeLabel(AiComparisonItemType t) => t switch
    {
        AiComparisonItemType.Material => "Malzeme",
        AiComparisonItemType.ManHours => "Adam-saat",
        AiComparisonItemType.ServiceFee => "Servis bedeli",
        AiComparisonItemType.GasUsage => "Gaz kullanımı",
        AiComparisonItemType.StoreMatch => "Mağaza eşleşmesi",
        AiComparisonItemType.GlycolUsage => "Glikol kullanımı",
        _ => t.ToString(),
    };

    private static readonly string[] ShortMonths = { "Oca", "Şub", "Mar", "Nis", "May", "Haz", "Tem", "Ağu", "Eyl", "Eki", "Kas", "Ara" };
    public static string PeriodText(int year, int month) => $"{ShortMonths[month - 1]} {year}";

    public static ControlAnalyticsResult Calculate(ControlAnalyticsFilter filter, IReadOnlyCollection<ControlCheckRow> allChecks)
    {
        var result = new ControlAnalyticsResult
        {
            CompanyOptions = allChecks.Select(c => c.CompanyName).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCulture).ToList(),
            RegionOptions = allChecks.Select(c => c.Region).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCulture).ToList(),
        };

        var checks = allChecks.Where(c => c.PeriodIndex >= filter.FromIndex && c.PeriodIndex <= filter.ToIndex)
            .Where(c => string.IsNullOrWhiteSpace(filter.CompanyName) || string.Equals(c.CompanyName, filter.CompanyName, StringComparison.OrdinalIgnoreCase))
            .Where(c => string.IsNullOrWhiteSpace(filter.Region) || string.Equals(c.Region, filter.Region, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var items = checks.SelectMany(c => c.Items.Where(i => !i.IsExcluded && i.Status != CheckItemControlStatus.KontrolDisi)
            .Select(i => (Check: c, Item: i))).ToList();

        // KPI
        var errorCount = items.Count(x => IsError(x.Item.Status));
        result.Kpis = new ControlKpis
        {
            CheckCount = checks.Count,
            CompletedChecks = checks.Count(c => c.IsCompleted),
            CompanyTotal = checks.Sum(c => c.CompanyTotal),
            CalculatedTotal = checks.Sum(c => c.CalculatedTotal),
            Difference = checks.Sum(c => c.Difference),
            OverbilledTotal = items.Where(x => x.Item.Difference > 0).Sum(x => x.Item.Difference!.Value),
            ItemCount = items.Count,
            ErrorItemCount = errorCount,
            PendingItemCount = items.Count(x => IsPending(x.Item.Status)),
            ErrorRatePercent = Rate(errorCount, items.Count),
        };

        // 1. Dönem bazında firma talebi / hesaplanan / fark — boş aylar da eksende yer alır
        for (var idx = filter.FromIndex; idx <= filter.ToIndex; idx++)
        {
            int y = idx / 12, m = idx % 12 + 1;
            var set = checks.Where(c => c.PeriodIndex == idx).ToList();
            result.Periods.Add(new ControlPeriodRow(y, m, PeriodText(y, m), set.Sum(c => c.CompanyTotal), set.Sum(c => c.CalculatedTotal), set.Sum(c => c.Difference)));
        }

        // 2. Kontrol sonucu dağılımı
        result.StatusDistribution = items.GroupBy(x => x.Item.Status)
            .OrderBy(g => g.Key == CheckItemControlStatus.Uygun ? 0 : 1).ThenByDescending(g => g.Count())
            .Select(g => new ControlStatusRow(StatusLabel(g.Key), g.Count(), g.Sum(x => x.Item.Difference ?? 0), StatusColor(g.Key)))
            .ToList();

        // 3. En çok sapan malzemeler (fazla talep edilen tutara göre)
        result.TopMaterials = items.Where(x => x.Item.Difference > 0)
            .GroupBy(x => x.Item.MaterialName.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new ControlMaterialRow(g.Key, g.Count(), g.Sum(x => x.Item.Difference!.Value),
                Math.Round(g.Average(x => x.Item.DifferencePercent ?? 0), 1)))
            .OrderByDescending(r => r.TotalDifference).Take(10).ToList();

        // 4. İl bazında fark ve hata oranı
        result.Cities = items.GroupBy(x => string.IsNullOrWhiteSpace(x.Item.StoreCity) ? "(İl bilinmiyor)" : x.Item.StoreCity!.Trim(),
                StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var err = g.Count(x => IsError(x.Item.Status));
                return new ControlCityRow(g.Key, g.Count(), err, Rate(err, g.Count()), g.Sum(x => x.Item.Difference ?? 0));
            })
            .OrderByDescending(r => r.TotalDifference).ThenByDescending(r => r.ErrorCount).Take(15).ToList();

        // 5. Firma karşılaştırması
        result.Companies = checks.GroupBy(c => string.IsNullOrWhiteSpace(c.CompanyName) ? "(Firma yok)" : c.CompanyName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var its = items.Where(x => g.Contains(x.Check)).Select(x => x.Item).ToList();
                var err = its.Count(i => IsError(i.Status));
                var withPct = its.Where(i => i.DifferencePercent.HasValue).ToList();
                return new ControlCompanyRow(g.Key, g.Count(), its.Count, Rate(err, its.Count),
                    withPct.Count == 0 ? 0 : Math.Round(withPct.Average(i => i.DifferencePercent!.Value), 1), g.Sum(c => c.Difference));
            })
            .OrderByDescending(r => r.TotalDifference).ToList();

        // 6. AI karşılaştırma kalitesi
        var ai = checks.SelectMany(c => c.AiResults).ToList();
        result.AiTotal = ai.Count;
        result.AiOverridden = ai.Count(a => a.UserOverridden);
        result.AiOverrideRatePercent = Rate(result.AiOverridden, ai.Count);
        result.AiStatuses = ai.GroupBy(a => AiStatusLabel(a.Status))
            .Select(g => new ControlAiStatusRow(g.Key, g.Count(), AiStatusColor(g.First().Status)))
            .OrderByDescending(r => r.Count).ToList();
        result.AiByType = ai.GroupBy(a => a.ItemType)
            .Select(g => new ControlAiTypeRow(AiItemTypeLabel(g.Key), g.Count(),
                g.Count(a => a.Status == AiComparisonStatus.Uygun), g.Count(a => a.Status == AiComparisonStatus.UygunDegil),
                g.Count(a => a.Status == AiComparisonStatus.Eksik), g.Count(a => a.Status == AiComparisonStatus.ManuelKontrol),
                g.Count(a => a.UserOverridden)))
            .OrderByDescending(r => r.Total).ToList();

        // Detaya inmek için kontrol listesi
        result.Checks = checks.OrderByDescending(c => c.PeriodIndex).ThenByDescending(c => c.CreatedAt)
            .Select(c => new ControlCheckListRow(c.Id, string.IsNullOrWhiteSpace(c.PeriodLabel) ? PeriodText(c.Year, c.Month) : c.PeriodLabel,
                c.CompanyName, c.Region, c.CategoryName, c.CompanyTotal, c.CalculatedTotal, c.Difference,
                c.Items.Count(i => !i.IsExcluded && IsError(i.Status)), c.IsCompleted))
            .ToList();

        return result;
    }

    private static decimal Rate(int part, int total) => total == 0 ? 0 : Math.Round(part * 100m / total, 1);
}
