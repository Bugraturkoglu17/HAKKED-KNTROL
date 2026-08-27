using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Kategoriden bağımsız, FORM granülaritesinde çalışan "Form Mutabakatı" özeti — Excel'deki her
/// form numarası "beklenen kayıt"tır (bkz. StoreFormReconciliationBuilder). Bu özet TAMAMEN persisted
/// veriden hesaplanır, bu yüzden kullanıcının "Onay ver" ile düzelttiği eski hatalar burada anında güncel
/// görünmelidir — bkz. özellikle Override_MagazaUyusmazligiKurtarilincaGercekSonucUreturVeOzetTemizlenir.</summary>
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

    /// <summary>Her satır bir hakediş kalemi (bir "beklenen form") üretir; malzeme sabit "TEST MALZEME"
    /// 1 adet 100 TL — fiyat/malzeme tarafı zaten uygun olsun ki testler saf olarak form/mağaza
    /// mutabakatını ölçsün.</summary>
    private static byte[] BuildWorkbook(params (string Code, string Name, DateTime Date, string FormNo)[] rows)
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

        for (int i = 0; i < rows.Length; i++)
        {
            var r = 2 + i;
            var (code, name, date, formNo) = rows[i];
            ws.Cell(r, 1).Value = code; ws.Cell(r, 2).Value = name; ws.Cell(r, 3).Value = date;
            ws.Cell(r, 4).Value = formNo; ws.Cell(r, 5).Value = "TEST MALZEME"; ws.Cell(r, 6).Value = 1;
            ws.Cell(r, 7).Value = 100; ws.Cell(r, 8).Value = 100;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static byte[] BuildTwoStoreWorkbook() => BuildWorkbook(
        ("1001", "Ankara MM", new DateTime(2026, 4, 23), "15527"),
        ("2002", "İzmir MM", new DateTime(2026, 4, 24), "16001"));

    private static async Task<(ProgressPaymentCheckService checkSvc, AppDbContext db, ProgressPaymentCheckDto check)> SeedCheckAsync(
        byte[] workbookBytes, HakedisCategory category = HakedisCategory.PeriodicMaintenance)
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

        using var stream = new MemoryStream(workbookBytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");

        var draft = await checkSvc.CreateDraftCheckAsync(list.Id, Company, Region, category);
        var check = await checkSvc.AttachExcelAsync(draft.Id, "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", workbookBytes, null, parsed);
        return (checkSvc, db, check);
    }

    private static AiVisionCallResultDto Success(AiPageExtractionDto extraction) => new()
    {
        Success = true, Extraction = extraction, RawJson = "{}",
        Usage = new AiTokenUsageDto { Model = "gpt-5.5", InputTokens = 100, OutputTokens = 50 },
    };

    [Fact]
    public async Task EksikForm_FormuOlmayanKayitTespitEdilirVeExportNotuYazilir()
    {
        var (checkSvc, db, check) = await SeedCheckAsync(BuildTwoStoreWorkbook());

        // Yalnızca Ankara MM'nin (form 15527) servis formu yükleniyor; İzmir MM'nin (16001) hiç formu yok.
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

        Assert.Equal(2, job.BeklenenFormSayisi);
        Assert.Equal(1, job.EslesenFormSayisi);
        Assert.Equal(1, job.EksikFormSayisi);

        var issues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.Contains(issues, i => i.IssueType == "Form Eksik" && i.StoreLabel.Contains("İzmir"));

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
    public async Task FazlaForm_ExceldeKarsiligiOlmayanFormTespitEdilirVeHicBirFormEslesmez()
    {
        var (_, db, check) = await SeedCheckAsync(BuildTwoStoreWorkbook());

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "99999", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "9999", NameRaw = "Bilinmeyen Mağaza", Confidence = 0.9m }, ServiceDate = "2026-04-23",
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        Assert.Equal(1, job.FazlaFormSayisi);
        Assert.Equal(2, job.BeklenenFormSayisi);
        Assert.Equal(0, job.EslesenFormSayisi);
        Assert.Equal(2, job.EksikFormSayisi); // yüklenen tek form hiçbir excel formuyla eşleşmedi, ikisi de "Form Eksik"

        // "Fazla form" yalnızca özet panelinde tekil sayı olarak görünür — mağaza bazlı satır üretmez
        // (bkz. plan §9: "Fazla formlardan dolayı hakediş Excelinde herhangi bir satıra hata yazma").
        var issues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.Contains(issues, i => i.IssueType == "Fazla Form" && i.Message.Contains("1 adet"));
    }

    [Fact]
    public async Task EksikForm_AyniMagazaninIkinciFormuYuklenmeseBileEksikGorunur()
    {
        // Ankara MM'nin 2 ayrı formu var (15527, 15528, farklı tarihlerde); yalnızca 15527 yükleniyor.
        // Form granülaritesi mağaza granülaritesinden farklı olduğu için mağaza "kısmen eşleşti" olsa
        // bile 15528 ayrı bir "Form Eksik" satırı üretmelidir.
        var workbook = BuildWorkbook(
            ("1001", "Ankara MM", new DateTime(2026, 4, 23), "15527"),
            ("1001", "Ankara MM", new DateTime(2026, 4, 25), "15528"),
            ("2002", "İzmir MM", new DateTime(2026, 4, 24), "16001"));
        var (_, db, check) = await SeedCheckAsync(workbook);

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

        Assert.Equal(3, job.BeklenenFormSayisi);
        Assert.Equal(1, job.EslesenFormSayisi);
        Assert.Equal(2, job.EksikFormSayisi); // 15528 (Ankara'nın kendi eksik formu) + 16001 (İzmir)

        var issues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.Contains(issues, i => i.IssueType == "Form Eksik" && i.Message.Contains("15528"));
    }

    [Fact]
    public async Task MukerrerZiyaret_AyniMagazaAyniTarihFarkliFormNumarasiUyariUretir()
    {
        // Excel'in kendi iç tutarlılığı: aynı mağaza + aynı tarihe bağlı 2 FARKLI form numarası varsa
        // uyarı üretilmeli — yüklenen formlarla ilgisi yok, salt Excel verisi üzerinden hesaplanır.
        var workbook = BuildWorkbook(
            ("1001", "Ankara MM", new DateTime(2026, 4, 23), "17790"),
            ("1001", "Ankara MM", new DateTime(2026, 4, 23), "17805"));
        var (_, db, check) = await SeedCheckAsync(workbook);

        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "17790", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "1001", Confidence = 0.9m }, ServiceDate = "2026-04-23",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        Assert.Equal(1, job.MukerrerZiyaretSayisi);
        var issues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.Contains(issues, i => i.IssueType == "Mükerrer Ziyaret" && i.Message.Contains("17790") && i.Message.Contains("17805"));
    }

    /// <summary>Kullanıcının şikayetinin kökü: bir "Mağaza Uyuşmazlığı" hatası "Onay ver" ile düzeltildiğinde
    /// (a) kategori kontrolü artık gerçek bir sonuç üretmeli (form/mağaza kapısı kalıcı olarak açılmalı,
    /// sadece hata Uygun'a boyanmamalı) ve (b) üst mutabakat özeti (ComputeSummaryAsync/GetStoreReconciliationIssuesAsync)
    /// bu eski hatayı ARTIK hiç göstermemeli — ör. "20732 mağaza uyuşmazlığı" örneği.</summary>
    [Fact]
    public async Task Override_MagazaUyusmazligiKurtarilincaGercekSonucUreturVeOzetTemizlenir()
    {
        // Mağaza adı bilerek yalnızca "Ankara MM" DEĞİL — bu ikisi de FormNumberMatcher'ın gürültü kelime
        // listesinde ("mm", "ankara") olduğu için normalize edilince boşa düşer ve isim karşılaştırması
        // atlanıp Mağaza Doğrulanamadı (Durum 6) üretilir. "Sincan" gürültü kelimesi olmadığı için isim
        // karşılaştırılabilir kalır ve gerçek bir Durum 4 (Mağaza Uyuşmazlığı) elde edilir.
        var workbook = BuildWorkbook(("1001", "Sincan MM", new DateTime(2026, 4, 23), "15527"));
        var (_, db, check) = await SeedCheckAsync(workbook);

        // Form no ve tarih doğru okunmuş ama mağaza kodu VE adı tamamen farklı bir mağazayı işaret ediyor
        // (Durum 4 — kod da isim de belirgin şekilde farklı) → "Mağaza Uyuşmazlığı", kategori kontrolü
        // hiç çalışmaz (gate bloklar).
        var vision = new FakeAiVisionClient(_ => Success(new AiPageExtractionDto
        {
            DocumentType = "SERVICE_FORM", FormNumber = "15527", FormNumberConfidence = 0.95m,
            Store = new AiStoreCandidateDto { CodeRaw = "9999", NameRaw = "Zeytinburnu Şubesi", Confidence = 0.9m },
            ServiceDate = "2026-04-23",
            Materials = new List<AiMaterialExtractionDto>
            {
                new() { RawName = "TEST MALZEME", NormalizedName = "test malzeme", Quantity = 1, Unit = "adet", Confidence = 0.9m },
            },
        }));
        var pipeline = BuildPipeline(db, vision, new FakePdfPageRasterizer(1));
        var job = await pipeline.RunAsync(check.Id, new List<(byte[], string)> { (new byte[] { 0 }, "servis.pdf") }, null, null, null);

        var beforeResults = await pipeline.GetComparisonResultsAsync(job.Id);
        // Gate mağazada bloklandığı için kategori kontrolü (malzeme karşılaştırması) hiç çalışmamış olmalı —
        // tek sonuç, gate hatasının kendisi olmalı.
        var mismatch = Assert.Single(beforeResults);
        Assert.Equal("Mağaza Uyuşmazlığı", mismatch.Description);
        Assert.Equal("UygunDegil", mismatch.Status);

        var beforeIssues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.Contains(beforeIssues, i => i.IssueType == "Mağaza Uyuşmazlığı");

        await pipeline.OverrideResultStatusAsync(mismatch.Id, note: "Doğru mağaza, kod OCR hatası.");

        var afterResults = await pipeline.GetComparisonResultsAsync(job.Id);
        // Gate artık açık: gerçek bir malzeme karşılaştırması üretilmiş olmalı (TEST MALZEME → Uygun).
        Assert.Contains(afterResults, r => r.ItemType == "Material" && r.Status == "Uygun");
        // Eski "Mağaza Uyuşmazlığı" hatası artık hiç üretilmiyor (gate override ile geçildiği için).
        Assert.DoesNotContain(afterResults, r => r.Description == "Mağaza Uyuşmazlığı" && r.Status != "Uygun");

        var afterIssues = await pipeline.GetStoreReconciliationIssuesAsync(job.Id);
        Assert.DoesNotContain(afterIssues, i => i.IssueType == "Mağaza Uyuşmazlığı");

        var afterJob = await pipeline.GetJobAsync(job.Id);
        Assert.Equal(1, afterJob!.BeklenenFormSayisi);
        Assert.Equal(1, afterJob.EslesenFormSayisi);
        Assert.Equal(0, afterJob.EksikFormSayisi);
    }
}
