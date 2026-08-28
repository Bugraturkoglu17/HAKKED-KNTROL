using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Glikol Kullanım hakedişine özel kontrol: Form No → Mağaza → Tarih → Glikol KG.
/// Yalnızca glikol kontrol edilir; formdaki diğer malzemeler (Excelde talep edilmese bile)
/// hiçbir uyarı üretmez (spec Test 7).</summary>
public class GlycolUsageComparisonStrategyTests
{
    private const string Company = "İNTİKOŞ";
    private const string Region = "İÇ ANADOLU";

    private static (AppDbContext db, ProgressPaymentCheck check) SeedCheck(AppDbContext db)
    {
        var list = new UnitPriceList { CompanyName = Company, Region = Region, Name = "Test Liste", IsActive = true, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        db.SaveChanges();

        var check = new ProgressPaymentCheck
        {
            UnitPriceListId = list.Id,
            CompanyName = Company, Region = Region,
            ClaimTypeName = "GLİKOL KULLANIM", Category = HakedisCategory.GlycolUsage, Year = 2026, Month = 4, PeriodLabel = "Nisan 2026",
            OriginalFileName = "test.xlsx", OriginalFilePath = "test.xlsx",
            Status = ProgressPaymentCheckStatus.Taslak, CreatedAt = DateTime.Now,
        };
        db.ProgressPaymentChecks.Add(check);
        db.SaveChanges();
        return (db, check);
    }

    private static AiAnalysisPipelineService BuildPipeline(AppDbContext db, FakeAiVisionClient vision, FakePdfPageRasterizer rasterizer)
    {
        var manHours = new ManHoursCalculator();
        var usage = new AiUsageTracker(db);
        var appPath = new FakeAppPathService();
        var categoryProfiles = new CategoryControlProfileRegistry(new ICategoryControlProfile[]
        {
            new CompressorReplacementProfile(), new GlycolUsageProfile(), new EvapReplacementProfile(),
            new PartialRenovationProfile(), new GasUsageProfile(), new MonitoringProfile(),
            new PeriodicMaintenanceProfile(), new AdditionalWorkProfile(),
        });
        var comparisonStrategies = new CategoryComparisonStrategyRegistry(new ICategoryComparisonStrategy[]
        {
            new DefaultCategoryComparisonStrategy(db), new GasUsageComparisonStrategy(db),
            new AdditionalWorkComparisonStrategy(db), new GlycolUsageComparisonStrategy(db),
        });
        return new AiAnalysisPipelineService(db, vision, rasterizer, manHours, usage, appPath, categoryProfiles, comparisonStrategies);
    }

    private static ProgressPaymentCheckItem GlycolItem(int checkId, string formNo, string storeCode, string storeName, DateTime date, decimal kg) => new()
    {
        ProgressPaymentCheckId = checkId,
        StoreCode = storeCode, StoreName = storeName,
        VisitDate = date, MaintenanceFormNo = formNo,
        IsServiceItem = false, OriginalMaterialName = "GLİKOL",
        Quantity = kg, Unit = "kg", CompanyUnitPrice = 50, CompanyLineTotal = 50 * kg,
        CreatedAt = DateTime.Now,
    };

    private static AiVisionCallResultDto Success(AiPageExtractionDto extraction) => new()
    {
        Success = true, Extraction = extraction, RawJson = "{}",
        Usage = new AiTokenUsageDto { Model = "gpt-5.5", InputTokens = 100, OutputTokens = 50 },
    };

    private static AiMaterialExtractionDto GlycolMaterial(decimal kg) =>
        new() { RawName = "GLİKOL", NormalizedName = "glikol", Quantity = kg, Unit = "kg", Confidence = 0.9m };

    /// <summary>TEST 1 — Form No, mağaza, tarih ve glikol kg tamamen aynı → UYGUN.</summary>
    [Fact]
    public async Task Test1_TumBilgilerAyniysa_UygunUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(25) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GlycolUsage");
        Assert.Equal("Uygun", result.Status);
    }

    /// <summary>TEST 2 — Form No Excelde yok VE mağaza da eşleşmiyor (yedek eşleştirme de bulamaz)
    /// → FORM HAKEDİŞTE BULUNAMADI.</summary>
    [Fact]
    public async Task Test2_FormNoExceldeYoksa_FormHakedisteBulunamadiUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "99999", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "9999", NameRaw = "Alakasız Mağaza", Confidence = 0.9m }, ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(25) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.Description == "Form Hakedişte Bulunamadı");
        Assert.Equal("Eksik", result.Status);
    }

    /// <summary>TEST 2b — Form No okunamıyor AMA mağaza kodu net eşleşiyor → yedek eşleştirme çalışır
    /// VE glikol miktarı da (ikincil alan olarak) bu satırda bağımsız hesaplanır.</summary>
    [Fact]
    public async Task Test2b_FormNoOkunamiyorAmaMagazaNetEslesiyorsa_YedekEslestirmeVeGlikolBirlikteCalisir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = null, FormNumberConfidence = 0.1m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(25) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = Assert.Single(results);
        Assert.Equal("Form No Yerine Mağazadan Eşleşti", result.Description);
        Assert.Equal("ManuelKontrol", result.Status);
        Assert.Equal("20730", result.HakedisValue); // yedek eşleştirmeyle bulunan gerçek form no
        Assert.Equal("25 kg", result.SecondaryHakedisValue); // glikol miktarı da bağımsız hesaplandı
        Assert.Equal("Uygun", result.SecondaryStatus);
    }

    /// <summary>TEST 3 — Form No aynı, mağaza farklı → MAĞAZA UYUŞMAZLIĞI.</summary>
    [Fact]
    public async Task Test3_MagazaFarkliysa_MagazaUyusmazligiUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1205", "Kızılay Ankara MM", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            // Kod da isim de açıkça farklı (Durum 4) — kod tek başına farklı olsaydı (isim benzerse)
            // OCR hatası varsayılıp eşleşme kabul edilirdi, bkz. FormNumberMatcher.CompareStore.
            Store = new AiStoreCandidateDto { CodeRaw = "1001", NameRaw = "Bahçelievler Ankara MM", Confidence = 0.9m }, ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(25) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.Description == "Mağaza Uyuşmazlığı");
        Assert.Equal("UygunDegil", result.Status);
    }

    /// <summary>TEST 4 — Form No ve mağaza aynı, tarih farklı → TARİH UYUŞMAZLIĞI.</summary>
    [Fact]
    public async Task Test4_TarihFarkliysa_TarihUyusmazligiUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(25) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.Description == "Tarih Uyuşmazlığı");
        Assert.Equal("UygunDegil", result.Status);
        // AŞAMA 1: tarih uyuşmazlığı olsa bile glikol miktarı BAĞIMSIZ olarak aynı satırda görünmeli
        // (form ve hakediş ikisi de 25kg olduğu için ikincil kontrol Uygun).
        Assert.Equal("25 kg", result.SecondaryHakedisValue);
        Assert.Equal("Uygun", result.SecondaryStatus);
    }

    /// <summary>TEST 4c — Hem tarih uyuşmazlığı HEM glikol miktarı farklı: satır yine TEK, ana konu
    /// Tarih Uyuşmazlığı olarak kalır ama ikincil alanlar glikol miktarının da UygunDegil olduğunu
    /// bağımsız olarak gösterir (bkz. AŞAMA 1 — Glikol Miktarı bağımsız kolon).</summary>
    [Fact]
    public async Task Test4c_TarihVeGlikolMiktariAyniAndaFarkliysa_IkiSorunDaAyniSatirdaGorunur()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 30));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05", // tarih farklı
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(20) }, // miktar da farklı
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = Assert.Single(results); // hâlâ tek satır, iki ayrı satır DEĞİL
        Assert.Equal("Tarih Uyuşmazlığı", result.Description); // ana konu tarih
        Assert.Equal("UygunDegil", result.Status);
        Assert.Equal("30 kg", result.SecondaryHakedisValue); // glikol miktarı da bağımsız görünüyor
        Assert.Equal("20 kg", result.SecondaryFormValue);
        Assert.Equal("UygunDegil", result.SecondaryStatus); // ikincil kontrol de kendi başına UygunDegil
    }

    /// <summary>TEST 4b (manuel onay regresyonu) — Tarih Uyuşmazlığı "Onay Ver" ile onaylandığında
    /// satır Glikol Miktarı'na DÖNÜŞMEMELİ; kontrol edilen alan (Description="Tarih Uyuşmazlığı") aynı
    /// kalmalı, yalnızca UserOverridden=true olmalı. Bu, manuel onayın kontrol tipini asla
    /// değiştiremeyeceği kuralının regresyon testidir (bkz. FormNumberMatcher.RecoverableError,
    /// AiAnalysisPipelineService.ApplyOverridesAsync).</summary>
    [Fact]
    public async Task Test4b_TarihUyusmazligiOnaylandiginda_GlikolMiktarinaDonusmez()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(25) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var before = await pipeline.GetComparisonResultsAsync(job.Id);
        var tarihResult = Assert.Single(before);
        Assert.Equal("Tarih Uyuşmazlığı", tarihResult.Description);
        Assert.False(tarihResult.UserOverridden);

        await pipeline.OverrideResultStatusAsync(tarihResult.Id, note: null);

        var after = await pipeline.GetComparisonResultsAsync(job.Id);
        var afterResult = Assert.Single(after); // hâlâ tek satır — Glikol Miktarı için AYRICA bir satır üretilmedi
        Assert.Equal("Tarih Uyuşmazlığı", afterResult.Description); // kontrol edilen alan DEĞİŞMEDİ
        Assert.Equal("Material", afterResult.ItemType); // GlycolUsage'a dönüşmedi
        Assert.True(afterResult.UserOverridden);
        Assert.Equal("05.04.2026", afterResult.FormValue); // kaynak form tarihi korunuyor (tooltip)
        Assert.Equal("02.04.2026", afterResult.HakedisValue); // kaynak Excel tarihi korunuyor
        Assert.Equal("25 kg", afterResult.SecondaryHakedisValue); // glikol miktarı (ikincil kontrol) da bozulmadı
        Assert.Equal("Uygun", afterResult.SecondaryStatus);
    }

    /// <summary>TEST 5 — Excel 30 kg, form 20 kg → GLİKOL MİKTARI UYUŞMAZLIĞI.</summary>
    [Fact]
    public async Task Test5_KgFarkliysa_GlikolMiktariUyusmazligiUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 30));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto> { GlycolMaterial(20) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GlycolUsage");
        Assert.Equal("UygunDegil", result.Status);
        Assert.Contains("30", result.Explanation);
        Assert.Contains("20", result.Explanation);
    }

    /// <summary>TEST 6 — Excelde glikol var, formda hiç glikol yok → GLİKOL FORMDA DOĞRULANAMADI.</summary>
    [Fact]
    public async Task Test6_FormdaGlikolYoksa_GlikolFormdaDogrulanamadiUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-02",
            // Glikol yok — sadece başka malzemeler var (bkz. Test 7 ile ortak senaryo).
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "Filtre", NormalizedName = "filtre", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GlycolUsage");
        Assert.Equal("Eksik", result.Status);
        Assert.Contains("Doğrulanamadı", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>TEST 7 — Formda glikol yanında 4 farklı malzeme daha var, Excelde yoklar → hiçbir ek uyarı üretilmez, sadece glikol satırı.</summary>
    [Fact]
    public async Task Test7_FormdaFazlaMalzemeVarsaAmaExceldeYoksa_HicbirEkUyariUretilmez()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GlycolItem(check.Id, "20730", "1001", "Mağaza X", new DateTime(2026, 4, 2), 25));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20730", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto>
            {
                GlycolMaterial(25),
                new() { RawName = "Gaz R404A", NormalizedName = "gaz r404a", Quantity = 2, Unit = "kg", Confidence = 0.9m },
                new() { RawName = "Filtre", NormalizedName = "filtre", Quantity = 1, Unit = "adet", Confidence = 0.9m },
                new() { RawName = "Dryer", NormalizedName = "dryer", Quantity = 1, Unit = "adet", Confidence = 0.9m },
                new() { RawName = "Vana", NormalizedName = "vana", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = Assert.Single(results); // yalnızca glikol satırı — gaz/filtre/dryer/vana için hiçbir sonuç yok
        Assert.Equal("GlycolUsage", result.ItemType);
        Assert.Equal("Uygun", result.Status);
    }
}
