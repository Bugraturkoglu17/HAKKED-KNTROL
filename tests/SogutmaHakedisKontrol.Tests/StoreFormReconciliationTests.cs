using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Kategoriden bağımsız "Mağaza Eşleşmesi" özeti — Excel'deki mağazalardan hangilerinin
/// formu var/yok, formlardaki hangi mağazaların Excel karşılığı yok (bkz. StoreFormReconciliationBuilder).</summary>
public class StoreFormReconciliationTests
{
    private const string Company = "TESTFIRMA";
    private const string Region = "TEST BÖLGE";

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

    private static byte[] BuildTwoStoreWorkbook()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("NİSAN");
        ws.Cell(1, 1).Value = "MAĞAZA KODU";
        ws.Cell(1, 2).Value = "MAĞAZA ADI";
        ws.Cell(1, 3).Value = "TARİH";
        ws.Cell(1, 4).Value = "FORM NO";
        ws.Cell(1, 5).Value = "MALZEME ADI";
        ws.Cell(1, 6).Value = "MİKTARI";
        ws.Cell(1, 7).Value = "FİYAT";
        ws.Cell(1, 8).Value = "TOPLAM";

        ws.Cell(2, 1).Value = "1001"; ws.Cell(2, 2).Value = "Ankara MM"; ws.Cell(2, 3).Value = new DateTime(2026, 4, 23);
        ws.Cell(2, 4).Value = "15527"; ws.Cell(2, 5).Value = "TEST MALZEME"; ws.Cell(2, 6).Value = 1;
        ws.Cell(2, 7).Value = 100; ws.Cell(2, 8).Value = 100;

        ws.Cell(3, 1).Value = "2002"; ws.Cell(3, 2).Value = "İzmir MM"; ws.Cell(3, 3).Value = new DateTime(2026, 4, 24);
        ws.Cell(3, 4).Value = "16001"; ws.Cell(3, 5).Value = "TEST MALZEME"; ws.Cell(3, 6).Value = 1;
        ws.Cell(3, 7).Value = 100; ws.Cell(3, 8).Value = 100;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static async Task<(ProgressPaymentCheckService checkSvc, AppDbContext db, ProgressPaymentCheckDto check)> SeedTwoStoreCheckAsync(
        HakedisCategory category = HakedisCategory.PeriodicMaintenance)
    {
        var db = TestDbFactory.Create();
        var matching = new MaterialMatchingService(db);
        var unitPriceList = new UnitPriceListService(db, matching);
        var appPath = new FakeAppPathService();
        var checkSvc = new ProgressPaymentCheckService(db, matching, appPath, unitPriceList);

        var list = new UnitPriceList { CompanyName = Company, Region = Region, Name = "Test Liste", IsActive = true, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        await db.SaveChangesAsync();

        // Fiyat tarafı zaten uygun olsun ki export notları saf olarak mağaza/form eşleşmesini test etsin.
        db.UnitPriceItems.Add(new UnitPriceItem
        {
            UnitPriceListId = list.Id, MaterialName = "TEST MALZEME", Price = 100m, Currency = "TRY",
            NormalizedName = matching.Normalize("TEST MALZEME"), IsActive = true, CreatedAt = DateTime.Now,
        });
        await db.SaveChangesAsync();

        var bytes = BuildTwoStoreWorkbook();
        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        Assert.Equal(2, parsed.Items.Count);

        var draft = await checkSvc.CreateDraftCheckAsync(list.Id, Company, Region, category);
        var check = await checkSvc.AttachExcelAsync(draft.Id, "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, null, parsed);
        return (checkSvc, db, check);
    }

    private static AiVisionCallResultDto Success(AiPageExtractionDto extraction) => new()
    {
        Success = true, Extraction = extraction, RawJson = "{}",
        Usage = new AiTokenUsageDto { Model = "gpt-5.5", InputTokens = 100, OutputTokens = 50 },
    };

    [Fact]
    public async Task EksikMagaza_FormuOlmayanMagazaTespitEdilirVeExportNotuYazilir()
    {
        var (checkSvc, db, check) = await SeedTwoStoreCheckAsync();

        // Yalnızca Ankara MM (form 15527) için form yükleniyor; İzmir MM'nin (16001) hiç formu yok.
        // Malzeme de forma dahil edilir ki satır 2'nin notsuz kalması saf mağaza/form eşleşmesini test etsin
        // (malzeme eşleşmesi ayrı bir kontrol boyutu — burada onu da Uygun yapıp gürültüyü önlüyoruz).
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15527", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-23",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        Assert.Equal(2, job.ExceldekiMagazaCount);
        Assert.Equal(1, job.TamEslesenMagazaCount);
        Assert.Equal(1, job.EksikMagazaCount);

        var issues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.Contains(issues, i => i.IssueType == "Eksik Mağaza" && i.StoreLabel.Contains("İzmir"));

        var outPath = await checkSvc.ExportControlledExcelAsync(check.Id);
        using var outWb = new XLWorkbook(outPath);
        var ws = outWb.Worksheet("NİSAN");
        // 8 orijinal kolon + KONTROL NOTU = 9. sütun. Satır 3 = İzmir MM (formsuz).
        var missingNote = ws.Cell(3, 9).GetString();
        Assert.Contains("servis formu yüklenmemiş", missingNote);
        // Ankara MM (satır 2, eşleşen) not almamalı.
        Assert.Equal(string.Empty, ws.Cell(2, 9).GetString());
    }

    [Fact]
    public async Task FazlaYabanciMagaza_ExceldeKarsiligiOlmayanFormTespitEdilir()
    {
        var (_, db, check) = await SeedTwoStoreCheckAsync();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "99999", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "9999", NameRaw = "Bilinmeyen Mağaza", Confidence = 0.9m }, ServiceDate = "2026-04-23",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        Assert.Equal(1, job.FazlaYabanciMagazaCount);
        Assert.Equal(1, job.EslesmeyenFormCount);
        Assert.Equal(0, job.TamEslesenMagazaCount);
        Assert.Equal(2, job.EksikMagazaCount); // hiçbir excel mağazası eşleşmedi

        var issues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.Contains(issues, i => i.IssueType == "Fazla/Yabancı Mağaza" && i.StoreLabel.Contains("Bilinmeyen"));
    }
}
