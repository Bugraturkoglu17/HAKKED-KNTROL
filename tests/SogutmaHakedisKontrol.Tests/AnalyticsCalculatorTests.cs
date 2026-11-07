using HakedisOtomasyon.Application.Analytics;
using SogutmaHakedisKontrol.Application.Analytics;
using SogutmaHakedisKontrol.Domain.Enums;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

public class ClaimAnalyticsCalculatorTests
{
    private static ClaimFormRow Form(int id, Period p, string file, bool done, int? store = 1, string cat = "Bakım / Onarım",
        DateTime? date = null, params ClaimItemRow[] items) => new()
    {
        ClaimId = 1, Period = p, CompanyName = "ACME", Category = cat, FormId = id, FilePath = file, IsCompleted = done,
        StoreId = store, StoreCode = store is null ? null : $"M{store}", StoreName = "Mağaza", Region = "İç Anadolu",
        FormDate = date, UploadedAt = new DateTime(p.Year, p.Month, 1), Items = items.ToList(),
    };

    private static ClaimItemRow Item(decimal total, decimal mat = 0, decimal lab = 0, bool invoice = false) =>
        new() { Description = "x", Quantity = 1, TotalPrice = total, MaterialPrice = mat, LaborPrice = lab, IsInvoiceBased = invoice };

    [Fact]
    public void Kpi_AyniDosyadanTureyenFormlarTekDosyaSayilir_TamamlanmamisToplamiTutar()
    {
        var p = new Period(2026, 10);
        var forms = new[]
        {
            Form(1, p, "a.pdf", done: true),
            Form(2, p, "a.pdf", done: true),
            Form(3, p, "a.pdf", done: false),   // İşleniyor / Bekliyor
            Form(4, p, "b.pdf", done: false),
            Form(5, p, "", done: false),         // elle eklenen, dosyasız
        };
        var claims = new[] { new ClaimHeaderRow(1, p, "ACME", false) };

        var r = ClaimAnalyticsCalculator.Calculate(new AnalyticsFilter(p, p), claims, forms);

        Assert.Equal(2, r.Kpis.UploadedFiles);
        Assert.Equal(5, r.Kpis.FormCount);
        Assert.Equal(2, r.Kpis.CompletedForms);
        Assert.Equal(3, r.Kpis.IncompleteForms);
        Assert.Equal(r.Kpis.FormCount, r.Kpis.CompletedForms + r.Kpis.IncompleteForms);
    }

    [Fact]
    public void Kpi_OncekiDonemeGoreDegisimHesaplanir()
    {
        var prev = new Period(2026, 9);
        var cur = new Period(2026, 10);
        var forms = new[] { Form(1, prev, "a", true, items: Item(1000)), Form(2, cur, "b", true, items: Item(1500)) };
        var claims = new[] { new ClaimHeaderRow(1, prev, "ACME", true), new ClaimHeaderRow(2, cur, "ACME", false) };

        var r = ClaimAnalyticsCalculator.Calculate(new AnalyticsFilter(cur, cur), claims, forms);

        Assert.Equal(1500, r.Kpis.TotalAmount);
        Assert.Equal(1000, r.Kpis.PreviousTotalAmount);
        Assert.Equal(50.0m, r.Kpis.ChangePercent);
        Assert.Equal(1, r.Kpis.ClaimCount);
    }

    [Fact]
    public void MaliyetKirilimi_ToplamKorunur_FaturaAyriSayilir()
    {
        var p = new Period(2026, 10);
        var forms = new[] { Form(1, p, "a", true, items: new[] { Item(300, mat: 100, lab: 200), Item(500, invoice: true), Item(50) }) };

        var r = ClaimAnalyticsCalculator.Calculate(new AnalyticsFilter(p, p), new[] { new ClaimHeaderRow(1, p, "ACME", false) }, forms);

        var row = Assert.Single(r.CostBreakdown);
        Assert.Equal(100, row.Material);
        Assert.Equal(200, row.Labor);
        Assert.Equal(500, row.Invoice);
        Assert.Equal(50, row.Other);
        Assert.Equal(850, row.Total);
        Assert.Equal(500, r.InvoiceShare.Single(x => x.Name == "Fatura bazlı").Amount);
    }

    [Fact]
    public void AylikKategoriSerisi_BosAylarSifirlaDoldurulur()
    {
        var from = new Period(2026, 8);
        var to = new Period(2026, 10);
        var forms = new[] { Form(1, to, "a", true, items: Item(100)) };

        var r = ClaimAnalyticsCalculator.Calculate(new AnalyticsFilter(from, to), new[] { new ClaimHeaderRow(1, to, "ACME", false) }, forms);

        Assert.Equal(3, r.PeriodLabels.Count);
        Assert.Equal(3, r.MonthlyByCategory.Count);
        Assert.Equal(new[] { 0m, 0m, 100m }, r.MonthlyByCategory.Select(x => x.Amount));
    }

    [Fact]
    public void TekrarlayanAriza_30GunIcindekiAyniMagazaAyniKategori()
    {
        var p = new Period(2026, 10);
        var forms = new[]
        {
            Form(1, p, "a", true, store: 7, date: new DateTime(2026, 10, 1)),
            Form(2, p, "b", true, store: 7, date: new DateTime(2026, 10, 12)),   // 11 gün → tekrar
            Form(3, p, "c", true, store: 7, date: new DateTime(2026, 12, 30)),   // 79 gün → değil
            Form(4, p, "d", true, store: 8, date: new DateTime(2026, 10, 2)),    // başka mağaza
        };

        var list = ClaimAnalyticsCalculator.BuildRepeatFaults(forms);

        var row = Assert.Single(list);
        Assert.Equal("M7", row.StoreCode);
        Assert.Equal(11, row.DaysBetween);
    }

    [Fact]
    public void Tuketim_OrtancaninIkiKatiniAsanMagazaIsaretlenir()
    {
        var p = new Period(2026, 10);
        ClaimItemRow Gas(decimal kg) => new() { Description = "R404A", ItemCode = "G1", Unit = "kg", Quantity = kg, TotalPrice = kg * 10 };
        ClaimFormRow GasForm(int id, int store, decimal kg) => new()
        {
            ClaimId = 1, Period = p, CompanyName = "ACME", Category = "Gaz Kullanım", IsGasUsage = true, FormId = id, FilePath = "g",
            IsCompleted = true, StoreId = store, StoreCode = $"M{store}", StoreName = "Mağaza", Items = new() { Gas(kg) },
        };
        var forms = new[] { GasForm(1, 1, 5), GasForm(2, 2, 6), GasForm(3, 3, 4), GasForm(4, 4, 20) };

        var list = ClaimAnalyticsCalculator.BuildConsumption(forms);

        var outlier = Assert.Single(list, r => r.IsOutlier);
        Assert.Equal("M4", outlier.StoreCode);
        Assert.Equal(11m, outlier.Threshold);   // ortanca 5,5 × 2
    }

    [Fact]
    public void Tuketim_UcMagazadanAzsaEsikUygulanmaz()
    {
        var p = new Period(2026, 10);
        var forms = new[] { 1, 2 }.Select(s => new ClaimFormRow
        {
            ClaimId = 1, Period = p, Category = "Glikol Kullanım", IsGlycolUsage = true, FormId = s, FilePath = "g", StoreId = s, StoreCode = $"M{s}",
            Items = new() { new ClaimItemRow { Description = "Glikol", ItemCode = "GL", Unit = "kg", Quantity = s * 100 } },
        });

        Assert.DoesNotContain(ClaimAnalyticsCalculator.BuildConsumption(forms), r => r.IsOutlier);
    }
}

public class ControlAnalyticsCalculatorTests
{
    private static ControlCheckRow Check(int id, int year, int month, string company, params ControlItemRow[] items) => new()
    {
        Id = id, Year = year, Month = month, CompanyName = company, Region = "Ankara",
        CompanyTotal = items.Sum(i => i.CompanyLineTotal), CalculatedTotal = items.Sum(i => i.CompanyLineTotal - (i.Difference ?? 0)),
        Difference = items.Sum(i => i.Difference ?? 0), Items = items.ToList(),
    };

    private static ControlItemRow Item(CheckItemControlStatus s, decimal total, decimal? diff = null, string mat = "Kompresör", string city = "Ankara", bool excluded = false) =>
        new() { Status = s, CompanyLineTotal = total, Difference = diff, DifferencePercent = diff is null || total == 0 ? null : diff / total * 100, MaterialName = mat, StoreCity = city, IsExcluded = excluded };

    [Fact]
    public void Kpi_HataOraniKontrolDisiVeHaricKalemleriSaymaz()
    {
        var c = Check(1, 2026, 10, "ACME",
            Item(CheckItemControlStatus.Uygun, 100, 0),
            Item(CheckItemControlStatus.FiyatHatasi, 200, 50),
            Item(CheckItemControlStatus.BirimUyusmazligi, 100, -10),
            Item(CheckItemControlStatus.OnayBekliyor, 100),
            Item(CheckItemControlStatus.KontrolDisi, 999, 999),
            Item(CheckItemControlStatus.FiyatHatasi, 999, 999, excluded: true));

        var r = ControlAnalyticsCalculator.Calculate(new ControlAnalyticsFilter(2026, 10, 2026, 10), new[] { c });

        Assert.Equal(4, r.Kpis.ItemCount);
        Assert.Equal(2, r.Kpis.ErrorItemCount);
        Assert.Equal(50.0m, r.Kpis.ErrorRatePercent);
        Assert.Equal(1, r.Kpis.PendingItemCount);
        Assert.Equal(50, r.Kpis.OverbilledTotal);   // yalnızca pozitif farklar
    }

    [Fact]
    public void Donemler_AraliktakiHerAyIcinSatirUretir_FiltreDisiDislanir()
    {
        var checks = new[]
        {
            Check(1, 2026, 8, "ACME", Item(CheckItemControlStatus.Uygun, 100, 0)),
            Check(2, 2026, 10, "ACME", Item(CheckItemControlStatus.FiyatHatasi, 300, 30)),
            Check(3, 2026, 10, "BETA", Item(CheckItemControlStatus.FiyatHatasi, 300, 30)),
            Check(4, 2025, 1, "ACME", Item(CheckItemControlStatus.FiyatHatasi, 300, 30)),
        };

        var r = ControlAnalyticsCalculator.Calculate(new ControlAnalyticsFilter(2026, 8, 2026, 10, CompanyName: "acme"), checks);

        Assert.Equal(new[] { "Ağu 2026", "Eyl 2026", "Eki 2026" }, r.Periods.Select(p => p.PeriodLabel));
        Assert.Equal(new[] { 100m, 0m, 300m }, r.Periods.Select(p => p.CompanyTotal));
        Assert.Equal(2, r.Kpis.CheckCount);
        Assert.Equal(new[] { "ACME", "BETA" }, r.CompanyOptions);
    }

    [Fact]
    public void EnCokSapanMalzemeler_FazlaTalebeGoreSiralanir()
    {
        var c = Check(1, 2026, 10, "ACME",
            Item(CheckItemControlStatus.FiyatHatasi, 100, 10, mat: "Evap"),
            Item(CheckItemControlStatus.FiyatHatasi, 100, 40, mat: "Kompresör"),
            Item(CheckItemControlStatus.FiyatHatasi, 100, 20, mat: "kompresör"),
            Item(CheckItemControlStatus.FiyatHatasi, 100, -50, mat: "Gaz"));

        var r = ControlAnalyticsCalculator.Calculate(new ControlAnalyticsFilter(2026, 10, 2026, 10), new[] { c });

        Assert.Equal(2, r.TopMaterials.Count);
        Assert.Equal(60, r.TopMaterials[0].TotalDifference);
        Assert.Equal(2, r.TopMaterials[0].Count);
        Assert.Equal("Evap", r.TopMaterials[1].MaterialName);
    }

    [Fact]
    public void AiKalitesi_DuzeltmeOraniHesaplanir()
    {
        var c = Check(1, 2026, 10, "ACME");
        c.AiResults.AddRange(new[]
        {
            new ControlAiRow { Status = AiComparisonStatus.Uygun, ItemType = AiComparisonItemType.Material },
            new ControlAiRow { Status = AiComparisonStatus.UygunDegil, ItemType = AiComparisonItemType.Material, UserOverridden = true },
            new ControlAiRow { Status = AiComparisonStatus.Eksik, ItemType = AiComparisonItemType.ServiceFee },
            new ControlAiRow { Status = AiComparisonStatus.ManuelKontrol, ItemType = AiComparisonItemType.ServiceFee },
        });

        var r = ControlAnalyticsCalculator.Calculate(new ControlAnalyticsFilter(2026, 10, 2026, 10), new[] { c });

        Assert.Equal(4, r.AiTotal);
        Assert.Equal(1, r.AiOverridden);
        Assert.Equal(25.0m, r.AiOverrideRatePercent);
        Assert.Equal(2, r.AiByType.Single(t => t.ItemType == "Malzeme").Total);
    }
}
