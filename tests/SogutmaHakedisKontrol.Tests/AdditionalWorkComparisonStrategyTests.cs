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

    /// <summary>KRİTİK KURAL — referans hakediş Excelidir: hakedişte hiç servis ücreti talep
    /// edilmemişse (yalnızca bir malzeme var), formda ziyaretin doğrulanmış olması tek başına bir
    /// servis ücreti talebi oluşturmaz — hiçbir ServiceFee sonucu üretilmemelidir.</summary>
    [Fact]
    public async Task HakediseServisUcretiTalepEdilmemisse_HicServiceFeeSonucuUretilmez()
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
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        Assert.DoesNotContain(results, r => r.ItemType == "ServiceFee");
    }

    /// <summary>Spec örneği: hakedişte yalnızca Filtre ve Yağ talep edilmiş; formda ayrıca Vana,
    /// Sensör, Kontaktör de bulunuyor. Excelde talep edilmeyen bu malzemeler için hiçbir sonuç
    /// üretilmemeli — sadece Filtre/Yağ kontrol edilir.</summary>
    [Fact]
    public async Task FormdaFazlaMalzemeVarsaAmaExceldeTalepEdilmemisse_HicbirSonucUretilmez()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "Filtre",
            Quantity = 2, Unit = "adet", CompanyUnitPrice = 10, CompanyLineTotal = 20,
            CreatedAt = DateTime.Now,
        });
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "Yağ",
            Quantity = 1, Unit = "adet", CompanyUnitPrice = 5, CompanyLineTotal = 5,
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
                new() { RawName = "Yağ", NormalizedName = "yag", Quantity = 1, Unit = "adet", Confidence = 0.9m },
                new() { RawName = "Vana", NormalizedName = "vana", Quantity = 1, Unit = "adet", Confidence = 0.9m },
                new() { RawName = "Sensör", NormalizedName = "sensor", Quantity = 1, Unit = "adet", Confidence = 0.9m },
                new() { RawName = "Kontaktör", NormalizedName = "kontaktor", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var materialResults = results.Where(r => r.ItemType == "Material").ToList();
        Assert.Equal(2, materialResults.Count); // yalnızca Filtre + Yağ — Vana/Sensör/Kontaktör için hiçbir sonuç yok
        Assert.All(materialResults, r => Assert.Equal("Uygun", r.Status));
        Assert.Contains(materialResults, r => r.Description == "Filtre");
        Assert.Contains(materialResults, r => r.Description == "Yağ");
    }

    /// <summary>Spec örneği: hakedişte Filtre 3 adet talep edilmiş, formda 2 adet doğrulanmış → Miktar Uyuşmazlığı.</summary>
    [Fact]
    public async Task ExceldeTalepEdilenMiktarFormdakindenFarkliysa_UygunDegilUretir()
    {
        using var db = TestDbFactory.Create();
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
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("UygunDegil", result.Status);
        Assert.Contains("3", result.Explanation);
        Assert.Contains("2", result.Explanation);
    }

    /// <summary>Spec örneği: hakedişte Solenoid Vana talep edilmiş ama formda hiç bulunmuyor → Formda Doğrulanamadı (Eksik).</summary>
    [Fact]
    public async Task ExceldeTalepEdilenMalzemeFormdaYoksa_EksikUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "Solenoid Vana",
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
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Eksik", result.Status);
        Assert.Equal("Solenoid Vana", result.Description);
        Assert.Contains("doğrulanamadı", result.Explanation, StringComparison.OrdinalIgnoreCase);
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
