using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Formda mağaza adı/kodu el yazısıyla belirsiz okunmuş olsa bile, form numarası hakediş
/// Excelinde bulunabiliyorsa gerçek mağaza adı ResolvedStoreLabel'e yazılmalı — manuel kontrol
/// listesinde kullanıcı "Sayfa X" yerine doğrudan mağaza adını görebilsin diye.</summary>
public class PageStoreLabelResolutionTests
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
            UnitPriceListId = list.Id, CompanyName = Company, Region = Region,
            ClaimTypeName = "PERİYODİK BAKIM", Year = 2026, Month = 4, PeriodLabel = "Nisan 2026",
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

    private static AiVisionCallResultDto Success(AiPageExtractionDto extraction) => new()
    {
        Success = true, Extraction = extraction, RawJson = "{}",
        Usage = new AiTokenUsageDto { Model = "gpt-5.5", InputTokens = 100, OutputTokens = 50 },
    };

    [Fact]
    public async Task FormdaMagazaBilgisiOkunamazsaBileFormNoIleHakedistenCozulur()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "3M", StoreName = "3M Migros",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "Test Malzeme",
            Quantity = 1, Unit = "adet", CompanyUnitPrice = 10, CompanyLineTotal = 10,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        // Form üzerinde mağaza kodu/adı hiç okunamamış (null) — yalnızca form numarası net.
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = null, ServiceDate = "2026-04-05", RequiresManualReview = true,
            Warnings = new List<string> { "Mağaza adı/kodu alanı el yazısı ve kısmen belirsizdir." },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var pages = await pipeline.GetPagesAsync(job.Id);
        var page = Assert.Single(pages);
        Assert.Null(page.StoreNameRaw);
        Assert.Equal("3M Migros", page.ResolvedStoreLabel);
    }

    [Fact]
    public async Task FormNoOkunamazsaResolvedStoreLabelNullKalir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = null, FormNumberConfidence = 0.1m,
            Store = null, RequiresManualReview = true,
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var pages = await pipeline.GetPagesAsync(job.Id);
        Assert.Null(Assert.Single(pages).ResolvedStoreLabel);
    }
}
