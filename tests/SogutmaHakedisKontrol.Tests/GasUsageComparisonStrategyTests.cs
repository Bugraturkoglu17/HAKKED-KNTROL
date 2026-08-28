using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Gaz Kullanım hakedişine özel kontrol: Form No → Mağaza → Tarih → Gaz KG.
/// Glikol Kullanım ile aynı desen (bkz. GlycolUsageComparisonStrategyTests) — mağaza/tarih
/// uyuşmazlığı olsa bile gaz miktarı bağımsız hesaplanıp aynı satırın ikincil alanlarına yazılır.</summary>
public class GasUsageComparisonStrategyTests
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
            ClaimTypeName = "GAZ KULLANIM", Category = HakedisCategory.GasUsage, Year = 2026, Month = 4, PeriodLabel = "Nisan 2026",
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

    private static ProgressPaymentCheckItem GasItem(int checkId, string formNo, string storeCode, string storeName, DateTime date, decimal kg) => new()
    {
        ProgressPaymentCheckId = checkId,
        StoreCode = storeCode, StoreName = storeName,
        VisitDate = date, MaintenanceFormNo = formNo,
        IsServiceItem = false, OriginalMaterialName = "SOĞUTUCU GAZ",
        Quantity = kg, Unit = "kg", CompanyUnitPrice = 679.12m, CompanyLineTotal = 679.12m * kg,
        CreatedAt = DateTime.Now,
    };

    private static AiVisionCallResultDto Success(AiPageExtractionDto extraction) => new()
    {
        Success = true, Extraction = extraction, RawJson = "{}",
        Usage = new AiTokenUsageDto { Model = "gpt-5.5", InputTokens = 100, OutputTokens = 50 },
    };

    private static AiMaterialExtractionDto GasMaterial(decimal kg) =>
        new() { RawName = "SOĞUTUCU GAZ", NormalizedName = "sogutucu gaz", Quantity = kg, Unit = "kg", Confidence = 0.9m };

    /// <summary>TEST 1 — Form No, mağaza, tarih ve gaz kg tamamen aynı → UYGUN.</summary>
    [Fact]
    public async Task Test1_TumBilgilerAyniysa_UygunUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20961", "710", "5M Ankara", new DateTime(2026, 5, 21), 40));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20961", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(40) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("40 kg", result.HakedisValue);
    }

    /// <summary>TEST 2 — Form No/mağaza doğru ama servis formu tarihi Excel'deki tarihten farklı
    /// (AŞAMA 1 deseni — Glikol ile aynı): satırın ANA konusu Tarih Uyuşmazlığı'dır, ama gaz miktarı
    /// bağımsız hesaplanıp ikincil alanlara (SecondaryFormValue/HakedisValue/Status) yazılır.</summary>
    [Fact]
    public async Task Test2_TarihUyusmazligindaGazMiktariIkincilAlanlaraYazilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20807", "3134", "MJET Hoşdere Ankara", new DateTime(2026, 5, 16), 10));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20807", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "3134", Confidence = 0.9m },
            ServiceDate = "2016-05-16", // yıl OCR hatası — Excel'de 2026
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(10) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = Assert.Single(results);
        Assert.Equal("Tarih Uyuşmazlığı", result.Description);
        Assert.Equal("UygunDegil", result.Status);
        Assert.Equal("10 kg", result.SecondaryFormValue);
        Assert.Equal("10 kg", result.SecondaryHakedisValue);
        Assert.Equal("Uygun", result.SecondaryStatus);
    }

    /// <summary>TEST 3 — Form No Excel'de yok, mağaza da eşleşmiyor → FORM HAKEDİŞTE BULUNAMADI
    /// (hardError — checkItemId yok, gaz miktarı hesaplanamaz).</summary>
    [Fact]
    public async Task Test3_FormNoExceldeYoksa_FormHakedisteBulunamadiUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20961", "710", "5M Ankara", new DateTime(2026, 5, 21), 40));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "99999", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "9999", NameRaw = "Tamamen Farklı Depo", Confidence = 0.9m },
            ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(40) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.Description == "Form Hakedişte Bulunamadı");
        Assert.Equal("Eksik", result.Status);
    }
}
