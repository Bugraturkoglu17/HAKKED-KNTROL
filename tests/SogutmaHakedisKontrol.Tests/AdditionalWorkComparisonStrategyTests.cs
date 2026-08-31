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

    // Gerçek hakediş dosyasında ("İÇ ANADOLU_İNTİKOŞ MİGROS SOĞUTMA İLAVE İŞLER") birebir doğrulanmış
    // metinler — MALZEME KODU S1/S2, adlar sonunda tek boşlukla bitiyor. storeCity varsayılan olarak
    // "ANKARA" — bu yüzden varsayılan çağrı şehir içi ile eşleşir (mevcut testlerin orijinal niyeti).
    private static ProgressPaymentCheckItem ServiceFeeItem(int checkId, string formNo, string storeCode, DateTime date,
        string feeCode = "S1", string? storeCity = "ANKARA") => new()
    {
        ProgressPaymentCheckId = checkId,
        StoreCode = storeCode, StoreName = "Ankara MM", StoreCity = storeCity,
        VisitDate = date, MaintenanceFormNo = formNo,
        IsServiceItem = true, OriginalItemCode = feeCode,
        OriginalMaterialName = feeCode == "S2" ? "1 EKIP ŞEHİR DIŞI SERVİS BEDELİ " : "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ ",
        Quantity = 1, Unit = "set", CompanyUnitPrice = 2750, CompanyLineTotal = 2750,
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
        // Kullanıcı talebi: "miktar hatalıysa düzeltebilmem lazım" — tablodaki Düzelt butonu bu alana
        // bakar (bkz. FormKontrol.razor CorrectMaterialQuantityAsync); eşleşen bir malzeme olduğu için dolu olmalı.
        Assert.All(materialResults, r => Assert.NotNull(r.MatchedMaterialId));
    }

    /// <summary>Kullanıcı talebi: gerçek bir servis formu fotoğrafından — teknisyen "Dolap Elektronik
    /// Fan Motoru" resmi adını bilmediği için formda yalnızca "Sütlük Fanı" yazmış. Tam metin benzerliği
    /// çok düşük çıkar ("dolap elektronik fan motoru" ~ "sutluk fani") ama "fan" ~ "fani" kelime/önek
    /// örtüşmesi var — bu yeterli sayılmalı, miktar (adet) üzerinden doğrulama devam etmeli.</summary>
    [Fact]
    public async Task TeknisyenResmiOlmayanIsimYazmissa_AnahtarKelimeOrtusmesiyleEslesir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "DOLAP ELEKTRONİK FAN MOTORU",
            Quantity = 2, Unit = "adet", CompanyUnitPrice = 27.18m, CompanyLineTotal = 54.36m,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "Sütlük Fanı", NormalizedName = "sutluk fani", Quantity = 2, Unit = "adet", Confidence = 0.7m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Uygun", result.Status);
    }

    /// <summary>Kullanıcı talebi: "formdaki 'hortum' ifadesi 'FLEX BORU' demek, bunu AI yorumlayabilir." —
    /// kelimeler kökten farklı olduğu için önek örtüşmesi yakalayamaz, elle onaylı eş anlamlı grubu ile eşleşmeli.</summary>
    [Fact]
    public async Task TeknisyenEsAnlamliGunlukKelimeYazmissa_EsAnlamliGrupOrtusmesiyleEslesir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "FLEX BORU",
            Quantity = 1, Unit = "adet", CompanyUnitPrice = 15m, CompanyLineTotal = 15m,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "Hortum", NormalizedName = "hortum", Quantity = 1, Unit = "adet", Confidence = 0.7m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Uygun", result.Status);
    }

    /// <summary>Kullanıcı talebi: "SOLENOİD GÖVDE"/"SOLENOİD BOBİNİ" formda "Selenoid valf" gibi yazılabilir
    /// (o/e yazım hatası, kelimeler kökten aynı ama farklı harfle yazılmış) — eş anlamlı grup örtüşmesiyle
    /// eşleşmeli, "gövde"/"valf" gibi farklı ek kelimeler eşleşmeyi engellememeli.</summary>
    [Fact]
    public async Task SolenoidSelenoidYaziHatasi_EsAnlamliGrupOrtusmesiyleEslesir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "SOLENOİD GÖVDE",
            Quantity = 1, Unit = "adet", CompanyUnitPrice = 200m, CompanyLineTotal = 200m,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "1/8 Selenoid valf", NormalizedName = "1/8 selenoid valf", Quantity = 1, Unit = "adet", Confidence = 0.7m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Uygun", result.Status);
    }

    /// <summary>Kullanıcı talebi: "FLEX BORU" formda "Filex" olarak da yazılabiliyor (yaygın yazım hatası).</summary>
    [Fact]
    public async Task FlexFilexYaziHatasi_EsAnlamliGrupOrtusmesiyleEslesir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "FLEX BORU",
            Quantity = 1, Unit = "adet", CompanyUnitPrice = 15m, CompanyLineTotal = 15m,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "Filex", NormalizedName = "filex", Quantity = 1, Unit = "adet", Confidence = 0.7m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Uygun", result.Status);
    }

    /// <summary>Aynı kural: "CAREL Modül Arayüz Adaptörü" için teknisyen yalnızca marka adını kısaltarak
    /// "TOP Card" yazmış (gerçek olayda "CAR" okunmuş) — "car" ~ "carel" önek örtüşmesiyle eşleşmeli.</summary>
    [Fact]
    public async Task MarkaAdiKisaltilmissa_OnekOrtusmesiyleEslesir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "CAREL MODÜL ARAYÜZ ADAPTÖRÜ",
            Quantity = 8, Unit = "adet", CompanyUnitPrice = 10, CompanyLineTotal = 80,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "Car", NormalizedName = "car", Quantity = 8, Unit = "adet", Confidence = 0.6m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Uygun", result.Status);
    }

    /// <summary>Gerçek bir servis formu fotoğrafından yakalandı: hakediş kalemi "CAREL DIŞ ORTAM PROBU
    /// METAL UÇLU" (3 adet); AYNI formda hem "Carel Dijital" (1 ad.) HEM "prob" (3 ad.) yazılı. Yalnızca
    /// marka adına ("carel") bakan bir eşleştirme yanlışlıkla "Carel Dijital"ı seçip miktar uyuşmazlığı
    /// (1 ≠ 3) üretiyordu — CAREL bu katalogda MODÜL/TERMOSTAT/PROB gibi tamamen farklı ürünlerde ortak
    /// geçtiği için marka adı tek başına ayırt edici değildir. Daha spesifik "prob"~"probu" örtüşmesi
    /// olduğu için doğru satır ("prob", 3 adet) seçilmeli ve miktar tam eşleşmeli → Uygun.</summary>
    [Fact]
    public async Task AyniMarkadanFarkliUrunlerVarsa_SpesifikKelimeyeSahipDogruUrunSecilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = false, OriginalMaterialName = "CAREL DIŞ ORTAM PROBU METAL UÇLU",
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
                new() { RawName = "Carel Dijitol", NormalizedName = "Carel Dijital", Quantity = 1, Unit = "Ad.", Confidence = 0.9m },
                new() { RawName = "prob", NormalizedName = "prob", Quantity = 3, Unit = "Ad.", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("3 Ad.", result.FormValue); // "prob" (3 ad.) seçilmiş olmalı, "Carel Dijital" (1 ad.) DEĞİL
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

    /// <summary>Kullanıcı talebi: "Excelde 2 yazan bir malzeme formda 400 yazsa da Excele göre para
    /// vereceğim için uygun say." — ödeme hakedişteki miktar üzerinden yapıldığından, formda hakedişten
    /// FAZLA miktar görünmesi fazla ödeme riski taşımaz (yönlü/asimetrik miktar kontrolü).</summary>
    [Fact]
    public async Task FormdakiMiktarHakedistenCokDahaFazlaysa_UygunUretir()
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
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "Filtre", NormalizedName = "filtre", Quantity = 400, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "Material");
        Assert.Equal("Uygun", result.Status);
        Assert.Contains("400", result.FormValue);
        Assert.Contains("hakedişteki", result.Explanation);
    }

    /// <summary>Kullanıcı talebi: "eğer 1 çalışan varsa 2 saat düşülmelidir... kuralı şuan yok" —
    /// gerçekte hesap doğru çalışıyordu (tek kişide 2 saat düşülüyordu) ama açıklama metni HER ZAMAN
    /// sabit "4 saat düşülerek" yazıyordu, bu da kullanıcının kuralın çalışmadığını sanmasına yol açtı.
    /// Açıklama artık gerçek kişi sayısı ve gerçekte düşülen saati göstermeli.</summary>
    [Fact]
    public async Task TekKisilikZiyaretteUyusmazlikAciklamasi_GercektenDusulenIkiSaatiGosterir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = true, OriginalItemCode = "S3", OriginalMaterialName = "ADAM SAAT GUNDUZ /GECE (>=2. GUN)",
            Quantity = 2, Unit = "saat", CompanyUnitPrice = 750, CompanyLineTotal = 1500,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Employees = new List<AiEmployeeExtractionDto>
            {
                new() { NameRaw = "Sedat Avcı", StartTime = "09:00", EndTime = "16:00", Confidence = 0.9m }, // 7 saat, tek kişi
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "ManHours");
        // 7 saat toplam, tek kişi olduğu için 2 saat düşülür → 5 ödenebilir; hakediş 2 istiyor → uyuşmuyor.
        Assert.Equal("UygunDegil", result.Status);
        Assert.Equal("5 saat", result.FormValue);
        Assert.Contains("(1 kişi) 2 saat düşülerek", result.Explanation);
        Assert.DoesNotContain("4 saat düşülerek", result.Explanation);
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

    /// <summary>Kullanıcı talebi: "İlave İşlerdeki tüm tarih hatalarını uygun sayalım, sadece ay hatası
    /// varsa kontrol edelim." — servis formu ayın 5'ini, hakediş Excel'i aynı ayın 20'sini gösteriyor
    /// (gün farklı, AY/YIL aynı) → Tarih Uyuşmazlığı ÜRETİLMEMELİ, kategori kontrolü normal çalışmalı.</summary>
    [Fact]
    public async Task AyniAyFarkliGunTarihi_TarihUyusmazligiUretmez()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(ServiceFeeItem(check.Id, "15001", "1001", new DateTime(2026, 4, 20)));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05", // gün farklı, ay/yıl aynı
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        Assert.DoesNotContain(results, r => r.Description == "Tarih Uyuşmazlığı");
        var fee = results.Single(r => r.ItemType == "ServiceFee");
        Assert.Equal("Uygun", fee.Status);
    }

    /// <summary>Kullanıcı talebi (revize): "İlave İşlerdeki tarihle alakalı hataları kaldır, bu kategoride
    /// önemli değil." — AY hatta YIL bile farklı olsa (Mart 2026 formu, Nisan 2026 hakedişi; hatta 2024
    /// formu, 2026 hakedişi) İlave İşler'de artık Tarih Uyuşmazlığı asla üretilmez.</summary>
    [Fact]
    public async Task FarkliAyVeyaYilTarihi_TarihUyusmazligiUretmez()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(ServiceFeeItem(check.Id, "15001", "1001", new DateTime(2026, 4, 20)));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2024-03-20", // hem ay hem yıl farklı
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        Assert.DoesNotContain(results, r => r.Description == "Tarih Uyuşmazlığı");
        var fee = results.Single(r => r.ItemType == "ServiceFee");
        Assert.Equal("Uygun", fee.Status);
    }

    /// <summary>Gerçek olayda yakalanan hata: TextNormalizationHelper.NormalizeName kelimeler arasındaki
    /// boşluğu KORUR (yalnızca dizi boşluğu tek boşluğa indirger) — bu yüzden eski ".Contains(\"adamsaat\")"
    /// (boşluksuz) kontrolü, gerçek metin "ADAM SAAT ..." (boşluklu) normalize edildiğinde ASLA
    /// eşleşmiyordu. Artık MALZEME KODU (S3/S4) ile eşleştiriliyor — gerçek dosyadan doğrulanmış.</summary>
    [Fact]
    public async Task AdamSaatKalemiGercekMalzemeKoduIleTespitEdilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = true, OriginalItemCode = "S3", OriginalMaterialName = "ADAM SAAT GUNDUZ /GECE (>=2. GUN)",
            Quantity = 8, Unit = "saat", CompanyUnitPrice = 750, CompanyLineTotal = 6000,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            Employees = new List<AiEmployeeExtractionDto>
            {
                new() { NameRaw = "Personel 1", StartTime = "08:00", EndTime = "16:00", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        // Asıl kontrol edilen şey: ADAM SAAT kaleminin (eski hatayla hiç tetiklenmeyen) HİÇ ATLANMADAN
        // tespit edilmiş olması — bir ManHours sonucu üretilmesi bunu tek başına kanıtlar.
        var result = results.Single(r => r.ItemType == "ManHours");
        // 08:00-16:00 = 8 saat çalışma, TEK kişilik ziyaret olduğu için 2 saat düşülür (kullanıcı talebi)
        // → 6 ödenebilir saat; hakediş 8 saat istemiş → uyuşmuyor.
        Assert.Equal("UygunDegil", result.Status);
        Assert.Equal("6 saat", result.FormValue);
        Assert.Equal("8 saat", result.HakedisValue);
    }

    /// <summary>Kullanıcı talebi: "2 kişinin 4 saat çalıştığı ... 2. kişinin verileri ilki ile aynı
    /// olduğunda '' işareti kullanılmıştır ... herkes eşit miktarda çalışır ... kaç kişi olduklarını
    /// tespit et ... 3 satırı da kaç saat çalıştığını kontrol etmesin ... eğer toplam saat altta
    /// yazmıyorsa 'kişi sayısı × 1 kişinin çalışma süresi' diyebilirsin." — formda alt toplam YAZILMADIĞI
    /// senaryoda, 2. personelin saatleri "" (aynı) işaretiyle yazıldığı için AI o satırın başlangıç/bitiş
    /// saatini OKUYAMAMIŞ olsa bile (null), toplam KİŞİ SAYISI × İLK KİŞİNİN SÜRESİYLE hesaplanmalı —
    /// eski satır-satır toplama yöntemiyle olduğu gibi tek satırın eksik okunması yüzünden AZ ÇIKMAMALIDIR.</summary>
    [Fact]
    public async Task IkinciPersonelinSaatiDittoIsaretiYuzundenOkunamazsa_KisiSayisiIleCarpilarakHesaplanir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = true, OriginalItemCode = "S3", OriginalMaterialName = "ADAM SAAT GUNDUZ /GECE (>=2. GUN)",
            Quantity = 4, Unit = "saat", CompanyUnitPrice = 750, CompanyLineTotal = 3000,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            // FormTotalHours BİLEREK verilmiyor — kullanıcının tarif ettiği "toplam saat altta yazmıyorsa" durumu.
            Employees = new List<AiEmployeeExtractionDto>
            {
                new() { NameRaw = "Personel 1", StartTime = "14:00", EndTime = "18:00", Confidence = 0.9m }, // 4 saat
                // "" (ditto) işareti yüzünden AI 2. personelin saatlerini okuyamamış — boş kalmış.
                new() { NameRaw = "Personel 2", StartTime = null, EndTime = null, Confidence = 0.5m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "ManHours");
        // Eski (satır satır toplama) yöntemle: yalnızca Personel 1'in 4 saati sayılır → 4 saat toplam,
        // 2 kişi kuralı gereği 4 saat düşülünce 0 ödenebilir saat çıkardı (YANLIŞ, gerçekte 8 adam-saat var).
        // Yeni yöntemle: 2 kişi × 4 saat = 8 toplam adam-saat, 4 saat kural düşülünce 4 ödenebilir —
        // hakedişin istediği 4 saatle tam örtüşüyor.
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("4 saat", result.FormValue);
    }

    /// <summary>Gerçek olayda yakalanan hata: aynı form iki ayrı analizde form_total_hours olarak bir kez
    /// doğru "8" (2 kişi × 4 saat), bir kez yanlış "3" (muhtemelen "Toplam: 3 Adam" kişi sayısı kutusuyla
    /// karıştırılmış) döndürdü — sistem yanlış "3"e körü körüne güvenip 0 ödenebilir saat hesapladı.
    /// Toplam süre hiçbir zaman TEK bir kişinin süresinden az olamayacağı için (toplam = herkesin
    /// toplamıdır), bariz şekilde düşük/imkânsız bir form_total_hours artık yok sayılıp kişi sayısı
    /// bazlı hesaba (2 × 4 = 8) düşülür.</summary>
    [Fact]
    public async Task FormTotalHoursTekKisininSuresindenAzsa_GuvenilmezSayilipKisiSayisiIleHesaplanir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
        {
            ProgressPaymentCheckId = check.Id, StoreCode = "1001", StoreName = "Ankara MM",
            VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
            IsServiceItem = true, OriginalItemCode = "S3", OriginalMaterialName = "ADAM SAAT GUNDUZ /GECE (>=2. GUN)",
            Quantity = 4, Unit = "saat", CompanyUnitPrice = 750, CompanyLineTotal = 3000,
            CreatedAt = DateTime.Now,
        });
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15001", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-05",
            FormTotalHours = 3m, // muhtemelen "3 Adam" kişi sayısı kutusuyla karışmış, imkansız derecede düşük
            Employees = new List<AiEmployeeExtractionDto>
            {
                new() { NameRaw = "Personel 1", StartTime = "14:00", EndTime = "18:00", Confidence = 0.9m }, // 4 saat
                new() { NameRaw = "Personel 2", StartTime = "14:00", EndTime = "18:00", Confidence = 0.9m }, // 4 saat
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "ManHours");
        // form_total_hours=3 yok sayılmalı (tek kişinin süresi olan 4'ten bile az) — 2×4=8 toplam,
        // 4 saat kural düşülünce 4 ödenebilir, hakedişin istediği 4 saatle örtüşüp Uygun olmalı.
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("4 saat", result.FormValue);
    }
}
