using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Kullanıcının bir AI karşılaştırma sonucunu manuel olarak "Uygun" onaylaması ve geri alması —
/// onay, sonuç recompute ile silinip yeniden üretilse bile kalıcı olmalı (bkz. AiComparisonOverride).</summary>
public class AiComparisonOverrideTests
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

    /// <summary>Hakedişte 3 adet talep edilmiş, formda 2 adet doğrulanmış → UygunDegil üretir. Kullanıcı
    /// manuel inceleyip onaylayınca Uygun'a döner, AI'nin orijinal sonucu OriginalStatusLabel'de saklanır.</summary>
    private static async Task<(AppDbContext db, ProgressPaymentCheck check, AiAnalysisPipelineService pipeline)> SeedMismatchAsync()
    {
        var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "Filtre",
            Quantity = 3, Unit = "adet", CompanyUnitPrice = 10, CompanyLineTotal = 30,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "Filtre", NormalizedName = "filtre", Quantity = 2, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);
        return (db, check, pipeline);
    }

    [Fact]
    public async Task OverrideResultStatusAsync_UygunDegilSonucuUygunaCevirirVeOrijinaliSaklar()
    {
        var (db, check, pipeline) = await SeedMismatchAsync();
        var job = await pipeline.GetLatestJobForCheckAsync(check.Id);
        var result = Assert.Single(await pipeline.GetComparisonResultsAsync(job!.Id));
        Assert.Equal("UygunDegil", result.Status);
        Assert.False(result.UserOverridden);

        await pipeline.OverrideResultStatusAsync(result.Id, note: "Manuel kontrol edildi, doğru.");

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var overridden = Assert.Single(results);
        Assert.Equal("Uygun", overridden.Status);
        Assert.True(overridden.UserOverridden);
        Assert.Equal("UygunDegil", overridden.OriginalStatusLabel);
    }

    [Fact]
    public async Task RevertOverrideAsync_OrijinalDurumaGeriDoner()
    {
        var (db, check, pipeline) = await SeedMismatchAsync();
        var job = await pipeline.GetLatestJobForCheckAsync(check.Id);
        var result = Assert.Single(await pipeline.GetComparisonResultsAsync(job!.Id));
        await pipeline.OverrideResultStatusAsync(result.Id, note: null);

        // OverrideResultStatusAsync artık sonunda RecomputeComparisonForJobAsync çalıştırır — bu, jobun
        // tüm AiComparisonResult satırlarını siler ve yeniden üretir, dolayısıyla satırın Id'si değişir
        // (gerçek kullanımda da UI her aksiyondan sonra LoadJobAsync ile listeyi tazeler). Revert için
        // güncel Id'yi yeniden okumak gerekir.
        var overridden = Assert.Single(await pipeline.GetComparisonResultsAsync(job.Id));
        await pipeline.RevertOverrideAsync(overridden.Id);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var reverted = Assert.Single(results);
        Assert.Equal("UygunDegil", reverted.Status);
        Assert.False(reverted.UserOverridden);
        Assert.Null(reverted.OriginalStatusLabel);
    }

    /// <summary>Onay, sonuç recompute ile (ör. bir malzeme düzeltmesi tetiklediğinde) tamamen silinip
    /// yeniden üretilse bile kalıcı olmalı — StoreFormReconciliationBuilder'daki gibi ayrı bir tablo
    /// kullanıldığı için ApplyOverridesAsync her recompute sonrası tekrar uygulanır.</summary>
    [Fact]
    public async Task Override_RecomputeSonrasiKaliciKalir()
    {
        var (db, check, pipeline) = await SeedMismatchAsync();
        var job = await pipeline.GetLatestJobForCheckAsync(check.Id);
        var result = Assert.Single(await pipeline.GetComparisonResultsAsync(job!.Id));
        await pipeline.OverrideResultStatusAsync(result.Id, note: null);

        // Bir malzeme düzeltmesi RecomputeComparisonForJobAsync'i tetikler — tüm sonuçlar silinip
        // yeniden üretilir, override'ın hayatta kalması gerekir.
        var page = await pipeline.GetPagesAsync(job.Id);
        var material = Assert.Single(page.Single().Materials);
        await pipeline.CorrectMaterialAsync(material.Id, correctedQuantity: 2, correctedUnit: "adet", note: "test düzeltme");

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var stillOverridden = Assert.Single(results);
        Assert.Equal("Uygun", stillOverridden.Status);
        Assert.True(stillOverridden.UserOverridden);
    }

    /// <summary>Export: override edilmiş bir satır artık "Uygun" sayıldığı için KONTROL NOTU almamalı
    /// (mevcut Export_FormKontroldeTespitEdilenSorunAyniSatiraNotOlarakEklenir testinin varyasyonu).</summary>
    [Fact]
    public async Task Export_OverrideEdilenSatirArtikKontrolNotuAlmaz()
    {
        var db = TestDbFactory.Create();
        var matching = new MaterialMatchingService(db);
        var unitPriceList = new UnitPriceListService(db, matching);
        var appPath = new FakeAppPathService();
        var checkSvc = new ProgressPaymentCheckService(db, matching, appPath, unitPriceList);

        var list = new UnitPriceList { CompanyName = "TESTFIRMA", Region = "TEST", Name = "Test Liste", IsActive = true, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        await db.SaveChangesAsync();
        db.UnitPriceItems.Add(new UnitPriceItem
        {
            UnitPriceListId = list.Id, MaterialName = "Uygun Malzeme", Price = 10m, Currency = "TRY",
            NormalizedName = matching.Normalize("Uygun Malzeme"), IsActive = true, CreatedAt = DateTime.Now,
        });
        await db.SaveChangesAsync();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("NİSAN");
        ws.Cell(1, 1).Value = "MAĞAZA ADI"; ws.Cell(1, 2).Value = "MALZEME ADI"; ws.Cell(1, 3).Value = "MİKTARI";
        ws.Cell(1, 4).Value = "FİYAT"; ws.Cell(1, 5).Value = "TOPLAM";
        ws.Cell(2, 1).Value = "Test Mağaza"; ws.Cell(2, 2).Value = "Uygun Malzeme";
        ws.Cell(2, 3).Value = 1; ws.Cell(2, 4).Value = 10; ws.Cell(2, 5).Value = 10;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();

        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        var draft = await checkSvc.CreateDraftCheckAsync(list.Id, "TESTFIRMA", "TEST", HakedisCategory.PeriodicMaintenance);
        var check = await checkSvc.AttachExcelAsync(draft.Id, "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, null, parsed);
        var item = Assert.Single(await checkSvc.GetItemsAsync(check.Id));

        var job = new AiAnalysisJob { ProgressPaymentCheckId = check.Id, Status = AiJobStatus.Completed, CreatedAt = DateTime.Now };
        db.AiAnalysisJobs.Add(job);
        await db.SaveChangesAsync();
        var comparisonResult = new AiComparisonResult
        {
            JobId = job.Id, ProgressPaymentCheckItemId = item.Id, StoreLabel = "Test Mağaza",
            ItemType = AiComparisonItemType.Material, Description = "Uygun Malzeme",
            Status = AiComparisonStatus.UygunDegil,
            Explanation = "Hakedişte 1 adet belirtilmiş, servis formunda bulunamamıştır.",
            CreatedAt = DateTime.Now,
        };
        db.AiComparisonResults.Add(comparisonResult);
        await db.SaveChangesAsync();

        var manHours = new ManHoursCalculator();
        var usage = new AiUsageTracker(db);
        var categoryProfiles = new CategoryControlProfileRegistry(Array.Empty<ICategoryControlProfile>());
        var comparisonStrategies = new CategoryComparisonStrategyRegistry(new ICategoryComparisonStrategy[] { new DefaultCategoryComparisonStrategy(db) });
        var pipeline = new AiAnalysisPipelineService(db, new FakeAiVisionClient(_ => Success(new AiPageExtractionDto())), new FakePdfPageRasterizer(0), manHours, usage, appPath, categoryProfiles, comparisonStrategies);

        await pipeline.OverrideResultStatusAsync(comparisonResult.Id, note: null);

        var outPath = await checkSvc.ExportControlledExcelAsync(check.Id);
        using var outWb = new XLWorkbook(outPath);
        var outWs = outWb.Worksheet("NİSAN");
        var noteText = outWs.Cell(2, 6).GetString(); // 5 orijinal kolon + KONTROL NOTU = 6

        Assert.True(string.IsNullOrWhiteSpace(noteText));
    }
}
