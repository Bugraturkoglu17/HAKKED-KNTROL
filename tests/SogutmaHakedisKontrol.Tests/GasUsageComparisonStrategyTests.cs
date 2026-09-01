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

    /// <summary>TEST 1b — kullanıcı talebi: "tüpler 5 kg'dan başlar", bu yüzden formda "2 kg" gibi
    /// fiziksel olarak imkansız bir okuma otomatik olarak "20 kg"ya düzeltilir (yalnızca durumu değil,
    /// GÖRÜNEN FormValue'yi de) — hakedişteki 20kg ile tam eşleşir, UYGUN olur.</summary>
    [Fact]
    public async Task Test1b_FizikselMinimumAltiOkuma_2Kg20KgOlarakDuzeltilirVeUygunUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20961", "710", "5M Ankara", new DateTime(2026, 5, 21), 20));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20961", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(2) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("20 kg", result.FormValue);
        Assert.Equal("20 kg", result.HakedisValue);
        Assert.Contains("5 kg", result.Explanation); // düzeltme notu açıklamada görünür kalmalı
    }

    /// <summary>TEST 1c — aynı kural: formda "1,5 kg" (ondalık ayıracı yanlış konumlanmış, gaz tüpleri
    /// 5 kg'nin altında olamaz) → otomatik "15 kg"ya düzeltilir, hakedişteki 15kg ile eşleşir, UYGUN olur.</summary>
    [Fact]
    public async Task Test1c_FizikselMinimumAltiOkuma_1_5Kg15KgOlarakDuzeltilirVeUygunUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20961", "710", "5M Ankara", new DateTime(2026, 5, 21), 15));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20961", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(1.5m) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("15 kg", result.FormValue);
        Assert.Equal("15 kg", result.HakedisValue);
        Assert.Contains("5 kg", result.Explanation);
    }

    /// <summary>TEST 1d — Gerçekten farklı bir miktar (10x ilişkisi YOK) hâlâ UYGUN DEĞİL kalmalı —
    /// tolerans yalnızca tam ×10/÷10 ilişkisini yakalar, genel bir gevşetme değildir.</summary>
    [Fact]
    public async Task Test1d_GercekMiktarFarki_OndalikIliskiYok_UygunDegilKalir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20961", "710", "5M Ankara", new DateTime(2026, 5, 21), 20));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20961", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(15) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("UygunDegil", result.Status);
    }

    /// <summary>TEST 1e — fiziksel-minimum düzeltmesi yapılsa BİLE hakedişle eşleşmeyebilir (gerçek bir
    /// hakediş/form farkı olabilir) — bu durumda Uygun Değil kalmalı ama açıklama, kullanıcının "1,5 kg
    /// değil aslında 15 kg" yorumunu zaten yaptığını göstermeli (düzeltilmiş değer FormValue'de görünür,
    /// ham okuma açıklamada kalır).</summary>
    [Fact]
    public async Task Test1e_FizikselMinimumAltiOkumaDuzeltilirAmaHakedisleYineDeUyusmazsaUygunDegilKalir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20961", "710", "5M Ankara", new DateTime(2026, 5, 21), 20));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20961", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(1.5m) }, // 15'e düzeltilir, hakediş 20 ister
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("UygunDegil", result.Status);
        Assert.Equal("15 kg", result.FormValue); // düzeltilmiş değer — ham "1,5 kg" değil
        Assert.Contains("1,5 kg", result.Explanation); // ham okuma şeffaflık için açıklamada kalmalı
    }

    /// <summary>TEST 2 — Form No/mağaza doğru ama servis formunun AYI Excel'deki aydan farklı (kullanıcı
    /// talebi: Gaz Kullanım'da yalnızca AY farkı hataya sayılır) — satırın ANA konusu Tarih Uyuşmazlığı'dır,
    /// ama gaz miktarı bağımsız hesaplanıp ikincil alanlara (SecondaryFormValue/HakedisValue/Status) yazılır.</summary>
    [Fact]
    public async Task Test2_AyFarkliysaTarihUyusmazligindaGazMiktariIkincilAlanlaraYazilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20807", "3134", "MJET Hoşdere Ankara", new DateTime(2026, 5, 16), 10));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20807", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "3134", Confidence = 0.9m },
            ServiceDate = "2026-04-16", // AY farklı (Nisan/Mayıs) — Excel'de Mayıs
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

    /// <summary>TEST 2b — kullanıcı talebi: "GAZ hakkedişlerinde sadece ay hatalarını baz alalım, gün ve
    /// yıl olanları doğru say" — servis formunun YILI VE GÜNÜ Excel'deki tarihten farklı olsa bile (AY
    /// aynıysa) artık bir Tarih Uyuşmazlığı üretilmez, normal tek satırlık Uygun sonucu döner.</summary>
    [Fact]
    public async Task Test2b_YilVeGunFarkliAmaAyAyniysa_TarihUyusmazligiUretmezUygunKalir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "20807", "3134", "MJET Hoşdere Ankara", new DateTime(2026, 5, 16), 10));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "20807", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "3134", Confidence = 0.9m },
            ServiceDate = "2016-05-20", // yıl VE gün farklı, AY aynı (Mayıs) — artık uyuşmazlık sayılmaz
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(10) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("10 kg", result.HakedisValue);
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

    /// <summary>TEST 4 — TEKRAR ZİYARET UYARISI (2 ziyaret): 05.06 → 20kg, 08.06 → 10kg (3 gün ara).
    /// İlk ziyarete (05.06) uyarı YAZILMAZ; ikinci ziyarete (08.06) "3 gün sonra tekrar gaz basılmıştır" uyarısı yazılır.</summary>
    [Fact]
    public async Task Test4_IkiZiyaretUcGunAra_IkinciZiyareteTekrarUyarisiYazilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.AddRange(
            GasItem(check.Id, "30001", "710", "5M Ankara", new DateTime(2026, 6, 5), 20),
            GasItem(check.Id, "30002", "710", "5M Ankara", new DateTime(2026, 6, 8), 10));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(idx => idx switch
        {
            0 => Success(new AiPageExtractionDto
            {
                DocumentType = "SERVICE_FORM", FormNumber = "30001", FormNumberConfidence = 0.95m,
                Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-06-05",
                Materials = new List<AiMaterialExtractionDto> { GasMaterial(20) },
            }),
            _ => Success(new AiPageExtractionDto
            {
                DocumentType = "SERVICE_FORM", FormNumber = "30002", FormNumberConfidence = 0.95m,
                Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-06-08",
                Materials = new List<AiMaterialExtractionDto> { GasMaterial(10) },
            }),
        });
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(2));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var warnings = results.Where(r => r.Description == "Tekrar Ziyaret Uyarısı").ToList();
        var warning = Assert.Single(warnings);
        Assert.Equal(new DateTime(2026, 6, 8), warning.VisitDate);
        Assert.Equal("Aynı mağazaya önceki gaz müdahalesinden 3 gün sonra tekrar gaz basılmıştır. Detaylı açıklama lazım.", warning.Explanation);
        Assert.Equal("ManuelKontrol", warning.Status);
        // Regresyon: bu satır sayfa/form eşleşmesinden bağımsız üretilir ama gerçek ziyarete ait bir
        // servis formu VARSA "Formu Göster" butonu görünmeli — eskiden SourcePageId hiç yazılmıyordu.
        Assert.False(string.IsNullOrEmpty(warning.FormFilePath));
    }

    /// <summary>TEST 5 — TEKRAR ZİYARET UYARISI (3 ziyaret): 05.06 → 20kg, 07.06 → 15kg (2 gün), 09.06 → 10kg (2 gün).
    /// İlk ziyarete uyarı yok; ikinci ziyarete "tekrar", üçüncü ziyarete "yeniden" ifadesiyle uyarı yazılır —
    /// her kayıt yalnızca KENDİSİNDEN BİR ÖNCEKİ ziyaretle karşılaştırılır (ilk ziyaretle değil).</summary>
    [Fact]
    public async Task Test5_UcZiyaretIkiserGunAra_IkinciVeUcuncuZiyareteUyariYazilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.AddRange(
            GasItem(check.Id, "30003", "710", "5M Ankara", new DateTime(2026, 6, 5), 20),
            GasItem(check.Id, "30004", "710", "5M Ankara", new DateTime(2026, 6, 7), 15),
            GasItem(check.Id, "30005", "710", "5M Ankara", new DateTime(2026, 6, 9), 10));
        db.SaveChanges();

        var forms = new (string FormNo, string Date, decimal Kg)[]
        {
            ("30003", "2026-06-05", 20), ("30004", "2026-06-07", 15), ("30005", "2026-06-09", 10),
        };
        var vision = new FakeAiVisionClient(idx => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = forms[idx].FormNo, FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = forms[idx].Date,
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(forms[idx].Kg) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(3));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var warnings = results.Where(r => r.Description == "Tekrar Ziyaret Uyarısı").OrderBy(r => r.VisitDate).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(new DateTime(2026, 6, 7), warnings[0].VisitDate);
        Assert.Equal("Aynı mağazaya önceki gaz müdahalesinden 2 gün sonra tekrar gaz basılmıştır. Detaylı açıklama lazım.", warnings[0].Explanation);
        Assert.Equal(new DateTime(2026, 6, 9), warnings[1].VisitDate);
        Assert.Equal("Aynı mağazaya önceki gaz müdahalesinden 2 gün sonra yeniden gaz basılmıştır. Detaylı açıklama lazım.", warnings[1].Explanation);
        // İlk ziyarete (05.06) hiçbir uyarı yazılmamalı.
        Assert.DoesNotContain(warnings, w => w.VisitDate == new DateTime(2026, 6, 5));
    }

    /// <summary>Gaz malzemesi servis formunda "gaz" kelimesi olmadan, doğrudan soğutucu akışkan koduyla
    /// yazılabilir (ör. "R404 A Soğutucu Akışkan") — yalnızca "gaz" araması bu satırı kaçırıp gerçek bir
    /// miktar varken bile "okunamadı" (Manuel Kontrol) üretiyordu. Artık tanınmalı ve doğrudan Uygun
    /// çıkmalı.</summary>
    [Fact]
    public async Task Test_SogutucuAkiskanAdliMalzeme_GazOlarakTaninirVeUygunUretir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "40010", "710", "5M Ankara", new DateTime(2026, 5, 21), 15));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "40010", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "R404 A Soğutucu Akışkan", NormalizedName = "R404A", Quantity = 15, Unit = "kg", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var results = await pipeline.GetComparisonResultsAsync(job.Id);
        var result = results.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("15 kg", result.HakedisValue);
    }

    /// <summary>Kullanıcının fiziksel formdan okuyup manuel girdiği miktar, hakedişteki değerle otomatik
    /// karşılaştırılmalı: eşleşirse Uygun, farklıysa Uygun Değil — kör bir onay değildir.</summary>
    [Fact]
    public async Task CorrectSingleItemQuantityAsync_GirilenMiktarHakedisleKarsilastirilirVeSonucBelirlenir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "50020", "710", "5M Ankara", new DateTime(2026, 5, 21), 20));
        db.SaveChanges();

        // Form yüklendi ama gaz kg bilgisi hiç okunamadı — Manuel Kontrol üretir.
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "50020", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var beforeResults = await pipeline.GetComparisonResultsAsync(job.Id);
        var pending = beforeResults.Single(r => r.ItemType == "GasUsage");
        Assert.Equal("ManuelKontrol", pending.Status);
        Assert.Equal("Okunamadı", pending.FormValue);

        // Kullanıcı formdan 20kg okuyup giriyor — hakedişteki 20kg ile eşleşir → Uygun.
        await pipeline.CorrectSingleItemQuantityAsync(pending.Id, 20m, "kg", "Formdan elle okundu.");
        var afterMatch = (await pipeline.GetComparisonResultsAsync(job.Id)).Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", afterMatch.Status);
        Assert.Equal("20 kg", afterMatch.FormValue);

        // Aynı satırı bu kez YANLIŞ bir miktarla düzeltirse (15kg) → Uygun Değil.
        await pipeline.CorrectSingleItemQuantityAsync(afterMatch.Id, 15m, "kg", null);
        var afterMismatch = (await pipeline.GetComparisonResultsAsync(job.Id)).Single(r => r.ItemType == "GasUsage");
        Assert.Equal("UygunDegil", afterMismatch.Status);
        Assert.Equal("15 kg", afterMismatch.FormValue);
    }

    /// <summary>Gerçek olay yerinde yakalanan hata: AI zaten (yanlış) bir miktar okumuşken kullanıcı
    /// düzeltme girerse, ExtractGasKg sayfadaki İLK "gaz" eşleşen malzemeyi (genelde AI'nın orijinal,
    /// yanlış okuduğu satır) seçip kullanıcının düzeltmesini sessizce yok sayıyordu — ekranda "Kaydet"
    /// tıklanıyor ama Uygun/Eksik/Fazla hiç değişmiyordu. Kullanıcı düzeltmesi artık her zaman önceliklidir.</summary>
    [Fact]
    public async Task CorrectSingleItemQuantityAsync_AiZatenYanlisMiktarOkumussaKullaniciDuzeltmesiKazanir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "60030", "710", "5M Ankara", new DateTime(2026, 5, 21), 10));
        db.SaveChanges();

        // AI formdan 200kg okumuş (gerçekte yanlış okuma) — hakedişte 10kg talep edilmiş → Uygun Değil.
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "60030", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto> { GasMaterial(200) },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var before = (await pipeline.GetComparisonResultsAsync(job.Id)).Single(r => r.ItemType == "GasUsage");
        Assert.Equal("UygunDegil", before.Status);
        Assert.Equal("200 kg", before.FormValue);

        // Kullanıcı formu bizzat okuyup 10kg olduğunu giriyor — hakedişteki 10kg ile eşleşir → Uygun.
        await pipeline.CorrectSingleItemQuantityAsync(before.Id, 10m, "kg", "AI yanlış okumuş, formda 10 kg yazıyor.");
        var after = (await pipeline.GetComparisonResultsAsync(job.Id)).Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", after.Status);
        Assert.Equal("10 kg", after.FormValue);
    }

    /// <summary>Gerçek olay yerinde yakalandı: AI, "Yol" (200 km yol masrafı) adlı bir servis formu
    /// satırını NormalizedName="gaz" olarak YANLIŞ sınıflandırmıştı — ExtractGasKg, RawName'de "gaz"
    /// geçmese bile NormalizedName'e güvenip bu satırı gaz miktarı sanıp 200 kg okumuştu (gerçek gaz
    /// malzemesi aynı sayfada 10 kg olarak doğru okunmuşken). Kullanıcı talebi net: yalnızca formda
    /// GERÇEKTEN "404" veya "gaz" ifadesi GEÇEN satırlar sayılmalı — AI'nın (yanlış olabilecek)
    /// NormalizedName sınıflandırması buna asla öncelik alamaz.</summary>
    [Fact]
    public async Task ExtractGasKg_YanlisSiniflandirilmisNormalizedNameGazDegil_GercekGazMalzemesiKullanilir()
    {
        using var db = TestDbFactory.Create();
        var (_, check) = SeedCheck(db);
        db.ProgressPaymentCheckItems.Add(GasItem(check.Id, "70040", "710", "5M Ankara", new DateTime(2026, 5, 21), 10));
        db.SaveChanges();

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "70040", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "710", Confidence = 0.9m }, ServiceDate = "2026-05-21",
            Materials = new List<AiMaterialExtractionDto>
            {
                // Gerçek olaydaki bire bir hata: RawName açıkça gaz DEĞİL ama NormalizedName yanlışlıkla "gaz".
                new() { RawName = "Yol", NormalizedName = "gaz", Quantity = 200, Unit = "km", Confidence = 0.8m },
                new() { RawName = "R404a Soğutucu Akışkan", NormalizedName = "gaz", Quantity = 10, Unit = "kg", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var result = (await pipeline.GetComparisonResultsAsync(job.Id)).Single(r => r.ItemType == "GasUsage");
        Assert.Equal("Uygun", result.Status);
        Assert.Equal("10 kg", result.FormValue); // "Yol"un 200'ü DEĞİL, gerçek gaz malzemesinin 10'u
    }
}
