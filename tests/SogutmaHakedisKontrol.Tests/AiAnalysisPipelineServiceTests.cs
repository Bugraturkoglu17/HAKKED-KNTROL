using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Spec §21 Test 3, Test 6, Test 7 — pipeline'ın tamamı, gerçek OpenAI çağrısı yapılmadan
/// sahte (deterministic) bir vision istemcisiyle uçtan uca test edilir.</summary>
public class AiAnalysisPipelineServiceTests
{
    private const string Company = "İNTİKOŞ";
    private const string Region = "İÇ ANADOLU";

    private static (AppDbContext db, ProgressPaymentCheck check) SeedCheck(AppDbContext db, HakedisCategory? category = null)
    {
        var list = new UnitPriceList { CompanyName = Company, Region = Region, Name = "Test Liste", IsActive = true, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        db.SaveChanges();

        var check = new ProgressPaymentCheck
        {
            UnitPriceListId = list.Id,
            CompanyName = Company, Region = Region,
            ClaimTypeName = "SABİT FİYAT", Category = category, Year = 2026, Month = 5, PeriodLabel = "Mayıs 2026",
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

    [Fact]
    public async Task Test3_PeriyodikBakimVarsa_SehirIciServisUcretiReddedilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);

        db.Stores.Add(new Store
        {
            CompanyName = Company, Region = Region, Code = "3336", Name = "MM Migros Bahçebey Çorum",
            NormalizedCode = TextNormalizationHelper.NormalizeCode("3336"),
            NormalizedName = TextNormalizationHelper.NormalizeName("MM Migros Bahçebey Çorum"),
            IsActive = true, CreatedAt = DateTime.Now,
        });
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id,
            StoreCode = "3336", StoreName = "MM Migros Bahçebey Çorum",
            VisitDate = new DateTime(2026, 5, 12),
            MaintenanceFormNo = "15527",
            IsServiceItem = true,
            OriginalMaterialName = "ŞEHİRİÇİ SERVİS ÜCRETİ",
            Quantity = 1, Unit = "adet",
            CompanyUnitPrice = 2750, CompanyLineTotal = 2750,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        const byte serviceMarker = 1;
        const byte maintenanceMarker = 2;
        var vision = new FakeAiVisionClient((pageIndex, source) => source switch
        {
            serviceMarker => Success(new AiPageExtractionDto
            {
                DocumentType = "SERVICE_FORM",
                FormNumber = "15527", FormNumberConfidence = 0.95m,
                Store = new AiStoreCandidateDto { CodeRaw = "3336", Confidence = 0.95m },
                ServiceDate = "2026-05-12",
            }),
            _ => Success(new AiPageExtractionDto
            {
                DocumentType = "PERIODIC_MAINTENANCE_FORM",
                FormNumber = "15527", FormNumberConfidence = 0.95m,
                Store = new AiStoreCandidateDto { CodeRaw = "3336", Confidence = 0.95m },
                MaintenanceDate = "2026-05-12",
            }),
        });

        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id,
            serviceForms: new List<(byte[], string)> { (new byte[] { serviceMarker }, "servis.pdf") },
            maintenanceFormsPdf: new byte[] { maintenanceMarker }, maintenanceFormsFileName: "bakim.pdf",
            progress: null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var feeResult = results.Single(r => r.ItemType == "ServiceFee");

        Assert.Equal("UygunDegil", feeResult.Status);
        Assert.Contains("periyodik bakım", feeResult.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test6_OkunamayanMiktarUydurulmaz_ManuelKontroleDuser()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.Stores.Add(new Store
        {
            CompanyName = Company, Region = Region, Code = "100", Name = "Test Mağaza",
            NormalizedCode = "100", NormalizedName = "test magaza", IsActive = true, CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM",
            Store = new AiStoreCandidateDto { CodeRaw = "100", Confidence = 0.9m },
            ServiceDate = "2026-05-01",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "R404A GAZ", Quantity = null, Confidence = 0.38m, RequiresManualReview = true },
            },
            RequiresManualReview = true,
        }));

        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var pages = await pipeline.GetPagesAsync(job.Id);
        var material = pages.Single().Materials.Single();

        Assert.Null(material.Quantity);
        Assert.True(material.RequiresManualReview);
        Assert.True(pages.Single().RequiresManualReview);
    }

    [Fact]
    public async Task Test7_BirSayfaBasarisizOlsaDaDigerleriKaybolmaz_SadeceBasarisizYenidenDenenir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.SaveChanges();

        // 3 sayfa: 0 ve 2 başarılı, 1 sürekli başarısız (retry mekanizması tükenir).
        var vision = new FakeAiVisionClient(pageIndex => pageIndex == 1
            ? new AiVisionCallResultDto { Success = false, ErrorMessage = "Simüle edilmiş API hatası" }
            : Success(new AiPageExtractionDto { DocumentType = "UNKNOWN" }));

        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(3));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        Assert.Equal(1, job.FailedPages);
        var pages = await pipeline.GetPagesAsync(job.Id);
        Assert.Equal(2, pages.Count(p => p.Status != nameof(AiPageStatus.Failed)));
        Assert.Equal(1, pages.Count(p => p.Status == nameof(AiPageStatus.Failed)));

        // Başarısız sayfa 3 kez denenmiş olmalı (MaxRetriesPerPage), başarılı sayfalar 1 kez.
        Assert.Equal(3, vision.CallCountForPage(1));
        Assert.Equal(1, vision.CallCountForPage(0));
        Assert.Equal(1, vision.CallCountForPage(2));
    }

    [Fact]
    public async Task Test8_SayfaTipleriPozisyondanBagimsizSiniflandirilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.Stores.Add(new Store
        {
            CompanyName = Company, Region = Region, Code = "500", Name = "Mix Test Mağaza",
            NormalizedCode = "500", NormalizedName = "mix test magaza", IsActive = true, CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        // Tek PDF içinde İcmal, Servis, Bakım, Sınıflandırılamayan sırasıyla karışık —
        // sınıflandırma yalnızca sayfa içeriğine göre yapılmalı, PDF'teki konuma göre değil.
        var vision = new FakeAiVisionClient(pageIndex => pageIndex switch
        {
            0 => Success(new AiPageExtractionDto { DocumentType = "SUMMARY" }),
            1 => Success(new AiPageExtractionDto
            {
                DocumentType = "SERVICE_FORM",
                Store = new AiStoreCandidateDto { CodeRaw = "500", Confidence = 0.9m },
                ServiceDate = "2026-06-01",
            }),
            2 => Success(new AiPageExtractionDto
            {
                DocumentType = "PERIODIC_MAINTENANCE_FORM",
                Store = new AiStoreCandidateDto { CodeRaw = "500", Confidence = 0.9m },
                MaintenanceDate = "2026-06-02",
            }),
            _ => Success(new AiPageExtractionDto { DocumentType = "UNKNOWN" }),
        });

        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(4));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "karisik.pdf") }, null, null, null);

        var pages = await pipeline.GetPagesAsync(job.Id);
        Assert.Equal("Summary", pages.Single(p => p.PageNumber == 1).DocumentType);
        Assert.Empty(pages.Single(p => p.PageNumber == 1).Materials);
        Assert.Equal("ServiceForm", pages.Single(p => p.PageNumber == 2).DocumentType);
        Assert.Equal("PeriodicMaintenanceForm", pages.Single(p => p.PageNumber == 3).DocumentType);
        Assert.Equal("Unknown", pages.Single(p => p.PageNumber == 4).DocumentType);
        Assert.True(pages.Single(p => p.PageNumber == 4).RequiresManualReview);

        Assert.Equal(1, job.SummaryPageCount);
        Assert.Equal(1, job.ClassifiedServiceFormPageCount);
        Assert.Equal(1, job.ClassifiedMaintenancePageCount);
        Assert.Equal(1, job.UnknownPageCount);
    }

    [Fact]
    public async Task Test9_CokluServisFormuPdfBirlestirilirVeBelgeAdiKorunur()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto { DocumentType = "SERVICE_FORM" }));

        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(2));
        var job = await pipeline.RunAsync(check.Id,
            serviceForms: new List<(byte[], string)>
            {
                (new byte[] { 1 }, "Servis_Formlari_1.pdf"),
                (new byte[] { 2 }, "Servis_Formlari_2.pdf"),
            },
            maintenanceFormsPdf: null, maintenanceFormsFileName: null, progress: null);

        Assert.Equal(4, job.TotalServiceFormPages);

        var pages = await pipeline.GetPagesAsync(job.Id);
        Assert.Equal("Servis_Formlari_1.pdf", pages.Single(p => p.PageNumber == 1).SourceFileName);
        Assert.Equal("Servis_Formlari_1.pdf", pages.Single(p => p.PageNumber == 2).SourceFileName);
        Assert.Equal("Servis_Formlari_2.pdf", pages.Single(p => p.PageNumber == 3).SourceFileName);
        Assert.Equal("Servis_Formlari_2.pdf", pages.Single(p => p.PageNumber == 4).SourceFileName);
    }

    /// <summary>Kategori bazlı mimari — GAZ KULLANIM kategorisinde hakediş 20 kg / form 10 kg ise
    /// GasUsageComparisonStrategy devreye girmeli ve UygunDegil sonucu üretmeli (bkz. plan §6).</summary>
    [Fact]
    public async Task GazKullanimKategorisi_HakedisVeFormKgFarkliysaUygunDegilUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db, HakedisCategory.GasUsage);
        db.Stores.Add(new Store
        {
            CompanyName = Company, Region = Region, Code = "500", Name = "Gaz Test Mağaza",
            NormalizedCode = "500", NormalizedName = "gaz test magaza", IsActive = true, CreatedAt = DateTime.Now,
        });
        db.SaveChanges();
        var store = db.Stores.Single(s => s.Code == "500");

        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id,
            StoreCode = "500", StoreName = "Gaz Test Mağaza", MatchedStoreId = store.Id,
            VisitDate = new DateTime(2026, 5, 20),
            MaintenanceFormNo = "8842",
            IsServiceItem = false,
            OriginalMaterialName = "GAZ R404A",
            Quantity = 20, Unit = "kg",
            CompanyUnitPrice = 100, CompanyLineTotal = 2000,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM",
            FormNumber = "8842", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "500", Confidence = 0.95m },
            ServiceDate = "2026-05-20",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "GAZ", NormalizedName = "gaz", Quantity = 10, Unit = "kg", Confidence = 0.9m },
            },
        }));

        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var gasResult = results.Single(r => r.ItemType == "GasUsage" && r.Description == "Gaz Miktarı (kg)");

        Assert.Equal("UygunDegil", gasResult.Status);
        Assert.Contains("20", gasResult.Explanation);
        Assert.Contains("10", gasResult.Explanation);
    }

    /// <summary>Ayrı mağaza listesi kaldırıldı — form kontrolü artık ilk yüklenen hakediş Excelini
    /// (form numarası → mağaza → tarih sırasıyla) kullanır. TEST 2-5: form no eşleştirme senaryoları.</summary>
    private static async Task<(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db, ProgressPaymentCheck check)> SeedFormMatchCheckAsync(
        string formNo = "15527", string storeCode = "1001", string storeName = "Ankara MM", DateTime? visitDate = null)
    {
        var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id,
            StoreCode = storeCode, StoreName = storeName,
            VisitDate = visitDate ?? new DateTime(2026, 4, 23),
            MaintenanceFormNo = formNo,
            IsServiceItem = false,
            OriginalMaterialName = "TEST MALZEME",
            Quantity = 1, Unit = "adet",
            CompanyUnitPrice = 100, CompanyLineTotal = 100,
            CreatedAt = DateTime.Now,
        });
        await db.SaveChangesAsync();
        return (db, check);
    }

    [Fact] // TEST 2 — Form Excel'de yok
    public async Task FormNo_ExceldeYoksa_FormHakedisteBulunamadiUretir()
    {
        var (db, check) = await SeedFormMatchCheckAsync(formNo: "15527");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "99999", FormNumberConfidence = 0.9m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-23",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material" && r.Description == "Form Hakedişte Bulunamadı");
        Assert.Equal("Eksik", result.Status);

        // Form no hiç Excel'de bulunamadığı için hangi Excel satırının buna karşılık geldiği belli değil —
        // dolayısıyla tek Excel satırı (form 15527) da ayrıca "Eksik Mağaza" (formu yok) olarak işaretlenir.
        Assert.Contains(results, r => r.ItemType == "StoreMatch" && r.Description == "Mağaza Eşleşmesi");
    }

    [Fact] // TEST 3 — Form numarası okunamıyor
    public async Task FormNo_Okunamiyorsa_ManuelKontrolUretirVeRastgeleEslesmez()
    {
        var (db, check) = await SeedFormMatchCheckAsync(formNo: "15527");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = null, FormNumberConfidence = 0.1m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-23",
            RequiresManualReview = true,
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material" && r.Description == "Form Numarası Okunamadı");
        Assert.Equal("ManuelKontrol", result.Status);

        // Form no okunamadığı için hangi Excel satırına ait olduğu belli değil — tek Excel satırı
        // (form 15527) da ayrıca "Eksik Mağaza" (formu yok) olarak işaretlenir.
        Assert.Contains(results, r => r.ItemType == "StoreMatch" && r.Description == "Mağaza Eşleşmesi");
    }

    [Fact] // TEST 4 — Form no eşleşti ama mağaza kodu VE adı da açıkça farklı (Durum 4)
    public async Task FormNo_EslestiAmaMagazaKoduVeAdiDaAcikcaFarkliysa_MagazaUyusmazligiUretir()
    {
        var (db, check) = await SeedFormMatchCheckAsync(formNo: "15527", storeCode: "1205", storeName: "Kızılay Ankara MM");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15527", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", NameRaw = "Bahçelievler Ankara MM", Confidence = 0.9m }, ServiceDate = "2026-04-23",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var result = Assert.Single(await pipeline.GetComparisonResultsAsync(job.Id));
        Assert.Equal("UygunDegil", result.Status);
        Assert.Equal("Mağaza Uyuşmazlığı", result.Description);
    }

    [Fact] // TEST 5 — Form no + mağaza eşleşti ama tarih farklı
    public async Task FormNo_VeMagazaEslestiAmaTarihFarkliysa_TarihUyusmazligiUretir()
    {
        var (db, check) = await SeedFormMatchCheckAsync(formNo: "15527", storeCode: "1001", visitDate: new DateTime(2026, 4, 25));
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15527", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-23",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var result = Assert.Single(await pipeline.GetComparisonResultsAsync(job.Id));
        Assert.Equal("UygunDegil", result.Status);
        Assert.Equal("Tarih Uyuşmazlığı", result.Description);
    }

    [Fact] // TEST 1 — Form no + mağaza + tarih hepsi eşleşince kategori kontrolü gerçekten çalışır
    public async Task FormNo_MagazaVeTarihEslesince_KategoriKontroluCalisir()
    {
        var (db, check) = await SeedFormMatchCheckAsync(formNo: "15527", storeCode: "1001", storeName: "Ankara MM");
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15527", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", NameRaw = "Ankara MM", Confidence = 0.95m }, ServiceDate = "2026-04-23",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        // Kontrol yönü EXCEL → FORM'dur: Description artık hakediş satırının kendi malzeme adı
        // (OriginalMaterialName = "TEST MALZEME"), formdan okunan ada göre değil.
        var result = results.Single(r => r.Description == "TEST MALZEME");
        Assert.Equal("Uygun", result.Status); // form no/mağaza/tarih doğrulandı, malzeme kategori kontrolü çalıştı ve uyumlu çıktı
    }

    private static AiVisionCallResultDto Success(AiPageExtractionDto extraction) => new()
    {
        Success = true,
        Extraction = extraction,
        RawJson = "{}",
        Usage = new AiTokenUsageDto { Model = "gpt-5.5", InputTokens = 100, OutputTokens = 50 },
    };
}
