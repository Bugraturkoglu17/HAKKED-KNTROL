namespace HakedisOtomasyon.Application.Analytics;

public sealed record ClaimHeaderRow(int ClaimId, Period Period, string CompanyName, bool Exported);

/// <summary>
/// Hakediş verisinden maliyet/trend ve operasyon göstergelerini üretir. Veritabanından bağımsızdır;
/// servis ham satırları okur, tüm hesap burada yapılır (birim test edilebilir).
/// </summary>
public static class ClaimAnalyticsCalculator
{
    public const int RepeatFaultWindowDays = 30;
    public const decimal OutlierMultiplier = 2m;
    public const int MinStoresForOutlier = 3;

    /// <param name="claims">Önceki dönem + seçili dönemi kapsayan hakediş başlıkları.</param>
    /// <param name="forms">Aynı aralıktaki (arşivlenmemiş) form satırları.</param>
    public static ClaimAnalyticsResult Calculate(AnalyticsFilter filter, IReadOnlyCollection<ClaimHeaderRow> claims, IReadOnlyCollection<ClaimFormRow> forms)
    {
        var result = new ClaimAnalyticsResult
        {
            Companies = claims.Select(c => c.CompanyName).Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCulture).ToList(),
            Regions = forms.Select(f => f.Region).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.CurrentCulture).ToList(),
        };

        bool CompanyOk(string name) => string.IsNullOrWhiteSpace(filter.CompanyName)
                                       || string.Equals(name, filter.CompanyName, StringComparison.OrdinalIgnoreCase);
        bool RegionOk(ClaimFormRow f) => string.IsNullOrWhiteSpace(filter.Region)
                                         || string.Equals(f.Region, filter.Region, StringComparison.OrdinalIgnoreCase);
        bool InRange(Period p, AnalyticsFilter r) => p >= r.From && p <= r.To;

        var prev = filter.Previous();
        var curClaims = claims.Where(c => InRange(c.Period, filter) && CompanyOk(c.CompanyName)).ToList();
        var curForms = forms.Where(f => InRange(f.Period, filter) && CompanyOk(f.CompanyName) && RegionOk(f)).ToList();
        var prevForms = forms.Where(f => InRange(f.Period, prev) && CompanyOk(f.CompanyName) && RegionOk(f)).ToList();

        var periods = Period.Range(filter.From, filter.To).ToList();
        result.PeriodLabels = periods.Select(p => p.Label).ToList();

        result.Kpis = BuildKpis(curClaims, curForms, prevForms);
        BuildMonthlyByCategory(result, periods, curForms);
        BuildCostBreakdown(result, periods, curForms);
        BuildCurrency(result, periods, curForms);
        result.TopStores = BuildTopStores(curForms);
        result.RepeatFaults = BuildRepeatFaults(curForms);
        result.Consumption = BuildConsumption(curForms);
        result.Kpis.RepeatFaultCount = result.RepeatFaults.Count;
        result.Kpis.ConsumptionOutlierCount = result.Consumption.Count(c => c.IsOutlier);
        return result;
    }

    private static decimal FormTotal(ClaimFormRow f) => f.Items.Sum(i => i.TotalPrice);

    private static ClaimKpis BuildKpis(List<ClaimHeaderRow> claims, List<ClaimFormRow> forms, List<ClaimFormRow> prevForms)
    {
        var total = forms.Sum(FormTotal);
        var prevTotal = prevForms.Sum(FormTotal);
        var completed = forms.Count(f => f.IsCompleted);
        return new ClaimKpis
        {
            // Aynı dosyadan türeyen kayıtlar tek dosya sayılır; elle eklenen (dosyasız) kayıtlar yüklenen dosya değildir.
            UploadedFiles = forms.Where(f => !string.IsNullOrWhiteSpace(f.FilePath))
                .Select(f => (f.ClaimId, Path: f.FilePath.Trim().ToLowerInvariant())).Distinct().Count(),
            FormCount = forms.Count,
            CompletedForms = completed,
            // Tamamlanmamış = tamamlanmamış her form (Bekliyor, İşleniyor, Eksik Bilgi); toplam her zaman tutar.
            IncompleteForms = forms.Count - completed,
            ClaimCount = claims.Count,
            ActiveClaims = claims.Count(c => !c.Exported),
            TotalAmount = total,
            PreviousTotalAmount = prevTotal,
            ChangePercent = prevTotal == 0 ? null : Math.Round((total - prevTotal) / prevTotal * 100m, 1),
        };
    }

    private static void BuildMonthlyByCategory(ClaimAnalyticsResult result, List<Period> periods, List<ClaimFormRow> forms)
    {
        var categories = forms.Select(f => f.Category).Distinct().OrderBy(c => c, StringComparer.CurrentCulture).ToList();
        result.Categories = categories;
        var sums = forms.GroupBy(f => (f.Period, f.Category)).ToDictionary(g => g.Key, g => g.Sum(FormTotal));
        foreach (var cat in categories)
            foreach (var p in periods)
                result.MonthlyByCategory.Add(new PeriodCategoryAmount(p.Label, cat, sums.GetValueOrDefault((p, cat))));
    }

    private static void BuildCostBreakdown(ClaimAnalyticsResult result, List<Period> periods, List<ClaimFormRow> forms)
    {
        decimal invTotal = 0, listTotal = 0;
        foreach (var p in periods)
        {
            decimal mat = 0, lab = 0, inv = 0, other = 0;
            foreach (var item in forms.Where(f => f.Period == p).SelectMany(f => f.Items))
            {
                if (item.IsInvoiceBased) { inv += item.TotalPrice; continue; }
                var unit = item.MaterialPrice + item.LaborPrice;
                if (unit > 0 && item.TotalPrice != 0)
                {
                    // Toplam fiyat (kur, otomatik kural vb. dahil) malzeme/işçilik oranında bölünür; toplamlar korunur.
                    var m = Math.Round(item.TotalPrice * item.MaterialPrice / unit, 2);
                    mat += m;
                    lab += item.TotalPrice - m;
                }
                else other += item.TotalPrice;
            }
            result.CostBreakdown.Add(new CostBreakdownRow(p.Label, mat, lab, inv, other));
            invTotal += inv;
            listTotal += mat + lab + other;
        }
        result.InvoiceShare = new List<NamedAmount>
        {
            new("Birim fiyat listesi", listTotal),
            new("Fatura bazlı", invTotal),
        };
    }

    private static void BuildCurrency(ClaimAnalyticsResult result, List<Period> periods, List<ClaimFormRow> forms)
    {
        var rows = forms.SelectMany(f => f.Items.Where(i => i.IsCurrencyBased && i.ExchangeRate is > 0)
                .Select(i => (f.Period, Code: (i.CurrencyCode ?? "USD").ToUpperInvariant(), Tl: i.TotalPrice, Foreign: i.TotalPrice / i.ExchangeRate!.Value)))
            .ToList();
        foreach (var code in rows.Select(r => r.Code).Distinct().OrderBy(c => c))
        {
            decimal? baseRate = null;
            foreach (var p in periods)
            {
                var set = rows.Where(r => r.Code == code && r.Period == p).ToList();
                var foreign = set.Sum(r => r.Foreign);
                var tl = set.Sum(r => r.Tl);
                var rate = foreign == 0 ? 0 : tl / foreign;
                if (baseRate is null && foreign != 0) baseRate = rate;
                // Kur etkisi: bu ayın döviz tutarı dönemin ilk kuruyla çevrilseydi oluşacak TL farkı
                var impact = baseRate is null || foreign == 0 ? 0 : tl - foreign * baseRate.Value;
                result.Currency.Add(new CurrencyPeriodRow(p.Label, code, Math.Round(foreign, 2), Math.Round(tl, 2), Math.Round(rate, 4), Math.Round(impact, 2)));
            }
        }
    }

    private static List<StoreCostRow> BuildTopStores(List<ClaimFormRow> forms, int take = 15) =>
        forms.Where(f => f.StoreId.HasValue)
            .GroupBy(f => f.StoreId!.Value)
            .Select(g =>
            {
                var first = g.First();
                return new StoreCostRow(g.Key, first.StoreCode ?? "", first.StoreName ?? "", first.City, g.Count(), g.Sum(FormTotal));
            })
            .OrderByDescending(s => s.TotalAmount).ThenByDescending(s => s.FormCount)
            .Take(take).ToList();

    /// <summary>Aynı mağazada aynı kategoride, aralarında en fazla 30 gün olan ardışık form çiftleri.</summary>
    public static List<RepeatFaultRow> BuildRepeatFaults(IEnumerable<ClaimFormRow> forms)
    {
        var list = new List<RepeatFaultRow>();
        foreach (var g in forms.Where(f => f.StoreId.HasValue).GroupBy(f => (f.StoreId!.Value, f.Category)))
        {
            var dated = g.Select(f => (Form: f, Date: (f.FormDate ?? f.UploadedAt).Date)).OrderBy(x => x.Date).ToList();
            for (var i = 1; i < dated.Count; i++)
            {
                var days = (dated[i].Date - dated[i - 1].Date).Days;
                if (days > RepeatFaultWindowDays) continue;
                var f = dated[i].Form;
                list.Add(new RepeatFaultRow(f.StoreCode ?? "", f.StoreName ?? "", g.Key.Category, dated[i - 1].Date, dated[i].Date, days));
            }
        }
        return list.OrderBy(r => r.DaysBetween).ThenByDescending(r => r.SecondDate).ToList();
    }

    /// <summary>
    /// Gaz / glikol tüketimi: mağaza başına toplam miktar ve form başına ortalama. Form başına ortalaması,
    /// aynı malzemede mağazalar ortancasının 2 katını aşan mağazalar "olağan dışı" işaretlenir (kaçak belirtisi olabilir).
    /// </summary>
    public static List<ConsumptionRow> BuildConsumption(IEnumerable<ClaimFormRow> forms, int take = 50)
    {
        var rows = forms.Where(f => (f.IsGasUsage || f.IsGlycolUsage) && f.StoreId.HasValue)
            .SelectMany(f => f.Items.Where(i => i.Quantity > 0 && !string.IsNullOrWhiteSpace(i.ItemCode)).Select(i => (Form: f, Item: i)))
            .ToList();

        var result = new List<ConsumptionRow>();
        foreach (var byMaterial in rows.GroupBy(r => r.Item.Description.Trim(), StringComparer.CurrentCultureIgnoreCase))
        {
            var stores = byMaterial.GroupBy(r => r.Form.StoreId!.Value).Select(g =>
            {
                var f = g.First().Form;
                var formCount = g.Select(x => x.Form.FormId).Distinct().Count();
                var qty = g.Sum(x => x.Item.Quantity);
                return (Form: f, Unit: g.First().Item.Unit, FormCount: formCount, Qty: qty, PerForm: qty / formCount);
            }).ToList();

            var threshold = stores.Count >= MinStoresForOutlier ? Median(stores.Select(s => s.PerForm)) * OutlierMultiplier : 0m;
            result.AddRange(stores.Select(s => new ConsumptionRow(s.Form.StoreCode ?? "", s.Form.StoreName ?? "", s.Form.City,
                byMaterial.Key, s.Unit, s.FormCount, s.Qty, Math.Round(s.PerForm, 2), Math.Round(threshold, 2),
                threshold > 0 && s.PerForm > threshold)));
        }
        return result.OrderByDescending(r => r.IsOutlier).ThenByDescending(r => r.TotalQuantity).Take(take).ToList();
    }

    public static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
