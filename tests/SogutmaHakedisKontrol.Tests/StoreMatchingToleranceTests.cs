using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Mağaza eşleştirme, mağaza kodundaki el yazısı OCR hatalarına karşı toleranslı olmalı —
/// form numarası + yeterli mağaza adı benzerliği (≥%50) tek başına eşleşme için yeterlidir.
/// Karar tablosu Durum 1-6 (bkz. FormNumberMatcher.CompareStore) burada test edilir.</summary>
public class StoreMatchingToleranceTests
{
    private const string Company = "İNTİKOŞ";
    private const string Region = "İÇ ANADOLU";

    private static (AppDbContext db, ProgressPaymentCheck check) SeedCheckWithItem(
        string storeCode, string storeName, DateTime visitDate = default)
    {
        var db = TestDbFactory.Create();
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

        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id,
            StoreCode = storeCode, StoreName = storeName,
            VisitDate = visitDate == default ? new DateTime(2026, 4, 2) : visitDate,
            MaintenanceFormNo = "20732",
            IsServiceItem = false, OriginalMaterialName = "TEST MALZEME",
            Quantity = 1, Unit = "adet", CompanyUnitPrice = 10, CompanyLineTotal = 10,
            CreatedAt = DateTime.Now,
        });
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

    /// <summary>Durum 2 — spec örneği: Excel "7956 – YUKARI DİKMEN MAH. ANKARA MM", form kodu el yazısı
    /// OCR ile "7856" (yanlış) okunmuş ama form mağaza adı da "Yukarı Dikmen" yazıyor → eşleşmeli,
    /// Mağaza Uyuşmazlığı ÜRETİLMEMELİ.</summary>
    [Fact]
    public async Task Durum2_KodOcrHatasiAmaMagazaAdiBenzerse_MagazaUyusmazligiUretilmez()
    {
        var (db, check) = SeedCheckWithItem("7956", "YUKARI DİKMEN MAH. ANKARA MM");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20732", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "7856", NameRaw = "YUKARI DİKMEN MİGROS", Confidence = 0.8m },
            ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        Assert.DoesNotContain(results, r => r.Description is "Mağaza Uyuşmazlığı" or "Mağaza Doğrulanamadı");
        Assert.Contains(results, r => r.Description == "TEST MALZEME" && r.Status == "Uygun");
    }

    /// <summary>Gerçek vaka: form "19983" — Excel kodu 7699, form kodu el yazısı OCR ile 7669 olarak
    /// yanlış okunmuş (tek rakam karışıklığı) VE mağaza adı sırası formda ("AKSARAY PARK SİTE MİGROS")
    /// ile Excel'de ("PARK SİTE AKSARAY MM MİGROS") farklı. Kelime sırası farklı olsa da ortak kelimeler
    /// (aksaray, park, site) tam örtüştüğü için eşleşmeli — Mağaza Uyuşmazlığı ÜRETİLMEMELİ.</summary>
    [Fact]
    public async Task GercekVaka_KodYanlisVeAdSirasiFarkliOlsaDaOrtakKelimelerEslesirse_Eslesir()
    {
        var (db, check) = SeedCheckWithItem("7699", "PARK SİTE AKSARAY MM MİGROS");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20732", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "7669", NameRaw = "AKSARAY PARK SİTE MİGROS", Confidence = 0.7m },
            ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        Assert.DoesNotContain(results, r => r.Description is "Mağaza Uyuşmazlığı" or "Mağaza Doğrulanamadı");
        Assert.Contains(results, r => r.Description == "TEST MALZEME" && r.Status == "Uygun");
    }

    /// <summary>Durum 3 — spec örneği: form kısaltılmış mağaza adı yazmış ("SİNCAN MM") ama mağaza kodu
    /// ("7845") Excel ile birebir aynı → tek başına kod eşleşmesi yeterli, isim kısa olsa da eşleşmeli.</summary>
    [Fact]
    public async Task Durum3_MagazaKoduAyniAmaAdiKisaltilmissa_Eslesir()
    {
        var (db, check) = SeedCheckWithItem("7845", "SELİN SK. SİNCAN ANKARA MM");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20732", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "7845", NameRaw = "SİNCAN MM", Confidence = 0.9m },
            ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        Assert.DoesNotContain(results, r => r.Description is "Mağaza Uyuşmazlığı" or "Mağaza Doğrulanamadı");
        Assert.Contains(results, r => r.Description == "TEST MALZEME" && r.Status == "Uygun");
    }

    /// <summary>Durum 5 — mağaza kodu formda hiç okunamamış (boş) ama mağaza adı benzerliği ≥%50 → eşleşmeli.</summary>
    [Fact]
    public async Task Durum5_KodOkunamiyorAmaAdBenzerse_Eslesir()
    {
        var (db, check) = SeedCheckWithItem("7845", "SELİN SK. SİNCAN ANKARA MM");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20732", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = null, NameRaw = "SİNCAN MM", Confidence = 0.5m },
            ServiceDate = "2026-04-02",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        Assert.DoesNotContain(results, r => r.Description is "Mağaza Uyuşmazlığı" or "Mağaza Doğrulanamadı");
        Assert.Contains(results, r => r.Description == "TEST MALZEME" && r.Status == "Uygun");
    }

    /// <summary>Durum 6 — ne mağaza kodu ne de mağaza adı doğrulanabiliyor (ikisi de boş/okunamıyor) →
    /// hemen "Uygun Değil" verilmemeli, Manuel Kontrol'e düşmeli.</summary>
    [Fact]
    public async Task Durum6_NeKodNeAdDogrulanabiliyorsa_ManuelKontrolUretir()
    {
        var (db, check) = SeedCheckWithItem("7845", "SELİN SK. SİNCAN ANKARA MM");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20732", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = null, NameRaw = null, Confidence = 0.2m },
            ServiceDate = "2026-04-02",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.Description == "Mağaza Doğrulanamadı");
        Assert.Equal("ManuelKontrol", result.Status);
    }
}
