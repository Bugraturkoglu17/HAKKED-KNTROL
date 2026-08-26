using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>İLAVE İŞLER hakedişine özel servis ücreti kuralları: her ziyaret için zorunlu,
/// aynı ziyarette mükerrer olamaz, farklı tarihli gerçek ayrı ziyaretler birbirini etkilemez.</summary>
public class AdditionalWorkComparisonStrategyTests
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
            ClaimTypeName = "İLAVE İŞLER", Category = HakedisCategory.AdditionalWork, Year = 2026, Month = 4, PeriodLabel = "Nisan 2026",
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
            new AdditionalWorkComparisonStrategy(db),
        });
        return new AiAnalysisPipelineService(db, vision, rasterizer, manHours, usage, appPath, categoryProfiles, comparisonStrategies);
    }

    private static ProgressPaymentCheckItem ServiceFeeItem(int checkId, string formNo, string storeCode, DateTime date) => new()
    {
        ProgressPaymentCheckId = checkId,
        StoreCode = storeCode, StoreName = "Ankara MM",
        VisitDate = date, MaintenanceFormNo = formNo,
        IsServiceItem = true, OriginalMaterialName = "ŞEHİRİÇİ SERVİS ÜCRETİ",
        Quantity = 1, Unit = "adet", CompanyUnitPrice = 2750, CompanyLineTotal = 2750,
        CreatedAt = DateTime.Now,
    };

    private static AiVisionCallResultDto Success(AiPageExtractionDto extraction) => new()
    {
        Success = true, Extraction = extraction, RawJson = "{}",
        Usage = new AiTokenUsageDto { Model = "gpt-5.5", InputTokens = 100, OutputTokens = 50 },
    };

    [Fact]
    public async Task ServisUcretiHicYoksa_ServisUcretiEksikUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "TEST MALZEME",
            Quantity = 1, Unit = "adet", CompanyUnitPrice = 10, CompanyLineTotal = 10,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var feeResult = results.Single(r => r.ItemType == "ServiceFee");
        Assert.Equal("Eksik", feeResult.Status);
        Assert.Equal("Servis Ücreti Eksik", feeResult.Description);
    }

    [Fact]
    public async Task AyniZiyaretteIkiServisUcretiVarsa_MukerrerServisUcretiUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(ServiceFeeItem(check.Id, "15001", "1001", new DateTime(2026, 4, 5)));
        db.ProgressPaymentCheckItems.Add(ServiceFeeItem(check.Id, "15001", "1001", new DateTime(2026, 4, 5)));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var feeResults = results.Where(r => r.ItemType == "ServiceFee").ToList();
        Assert.Equal(2, feeResults.Count);
        Assert.All(feeResults, r => Assert.Equal("UygunDegil", r.Status));
        Assert.All(feeResults, r => Assert.Equal("Mükerrer Servis Ücreti", r.Description));
    }

    [Fact]
    public async Task FarkliTarihliIkiZiyaretinServisUcreti_MukerrerSayilmazIkisiDeUygun()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(ServiceFeeItem(check.Id, "15001", "1001", new DateTime(2026, 4, 5)));
        db.ProgressPaymentCheckItems.Add(ServiceFeeItem(check.Id, "15120", "1001", new DateTime(2026, 4, 10)));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(pageIndex => pageIndex == 0
            ? Success(new AiPageExtractionDto
            {
                DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
                Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            })
            : Success(new AiPageExtractionDto
            {
                DocumentType = "SERVICE_FORM", FormNumber = "15120", FormNumberConfidence = 0.95m,
                Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-10",
            }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(2));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var feeResults = results.Where(r => r.ItemType == "ServiceFee").ToList();
        Assert.Equal(2, feeResults.Count);
        Assert.All(feeResults, r => Assert.Equal("Uygun", r.Status));
        Assert.DoesNotContain(feeResults, r => r.Description == "Mükerrer Servis Ücreti");
    }
}
