using ClosedXML.Excel;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Geçmiş kontrolleri temizleme (silme) özelliği — kontrol kaydı ve tüm bağlı verilerin
/// (kalemler, denetim izi, AI analiz job/sayfa/karşılaştırma/kullanım kayıtları) silindiğini,
/// diskteki ilişkili dosyaların da temizlendiğini doğrular.</summary>
public class ProgressPaymentCheckDeleteTests
{
    private static (ProgressPaymentCheckService svc, SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db) CreateService()
    {
        var db = TestDbFactory.Create();
        var matching = new MaterialMatchingService(db);
        var unitPriceList = new UnitPriceListService(db, matching);
        var appPath = new FakeAppPathService();
        var svc = new ProgressPaymentCheckService(db, matching, appPath, unitPriceList);
        return (svc, db);
    }

    private static byte[] BuildHakedisWorkbook()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("NİSAN");
        ws.Cell(1, 1).Value = "MAĞAZA ADI"; ws.Cell(1, 2).Value = "MALZEME ADI"; ws.Cell(1, 3).Value = "MİKTARI";
        ws.Cell(1, 4).Value = "FİYAT"; ws.Cell(1, 5).Value = "TOPLAM";
        ws.Cell(2, 1).Value = "Test Mağaza"; ws.Cell(2, 2).Value = "Test Malzeme";
        ws.Cell(2, 3).Value = 1; ws.Cell(2, 4).Value = 10; ws.Cell(2, 5).Value = 10;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task DeleteCheckAsync_KaydıVeBağlıTümVeriyiSilerVeDosyalarıTemizler()
    {
        var (svc, db) = CreateService();
        var list = new UnitPriceList { CompanyName = "TESTFIRMA", Region = "TEST", Name = "Test Liste", IsActive = true, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        await db.SaveChangesAsync();

        var bytes = BuildHakedisWorkbook();
        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        var draft = await svc.CreateDraftCheckAsync(list.Id, "TESTFIRMA", "TEST", HakedisCategory.PeriodicMaintenance);
        var check = await svc.AttachExcelAsync(draft.Id, "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, null, parsed);
        var item = Assert.Single(await svc.GetItemsAsync(check.Id));

        Assert.True(File.Exists(check.OriginalFilePath));

        // Denetim izi (CheckItemActionLog) — EF ilişkisi olmayan tablo, elle temizlenmesi gerekiyor.
        db.CheckItemActionLogs.Add(new CheckItemActionLog
        {
            ProgressPaymentCheckId = check.Id, ProgressPaymentCheckItemId = item.Id,
            Action = "Duzelt", Note = "test", CreatedAt = DateTime.Now,
        });

        // AI job + sayfa + karşılaştırma sonucu + kullanım kaydı — cascade + elle temizlenen tabloların karışımı.
        var job = new AiAnalysisJob { ProgressPaymentCheckId = check.Id, Status = AiJobStatus.Completed, CreatedAt = DateTime.Now };
        db.AiAnalysisJobs.Add(job);
        await db.SaveChangesAsync();

        var page = new AiDocumentPage
        {
            JobId = job.Id, SourceKind = AiDocumentSource.ServiceForm, PageNumber = 1,
            Status = AiPageStatus.Succeeded, DocumentType = AiDocumentType.ServiceForm, CreatedAt = DateTime.Now,
        };
        db.AiDocumentPages.Add(page);
        await db.SaveChangesAsync();

        db.AiComparisonResults.Add(new AiComparisonResult
        {
            JobId = job.Id, SourcePageId = page.Id, ProgressPaymentCheckItemId = item.Id, StoreLabel = "Test Mağaza",
            ItemType = AiComparisonItemType.Material, Description = "Test Malzeme",
            Status = AiComparisonStatus.Uygun, Explanation = "test", CreatedAt = DateTime.Now,
        });
        db.AiUsageLogs.Add(new AiUsageLog { JobId = job.Id, Model = "gpt-5.5", InputTokens = 10, RequestedAt = DateTime.Now });
        await db.SaveChangesAsync();

        var checkId = check.Id;
        var originalPath = check.OriginalFilePath!;

        await svc.DeleteCheckAsync(checkId);

        Assert.Null(await svc.GetByIdAsync(checkId));
        Assert.Empty(await svc.GetItemsAsync(checkId));
        Assert.False(File.Exists(originalPath));
        Assert.False(db.CheckItemActionLogs.Any(a => a.ProgressPaymentCheckId == checkId));
        Assert.False(db.AiAnalysisJobs.Any(j => j.ProgressPaymentCheckId == checkId));
        Assert.False(db.AiDocumentPages.Any(p => p.JobId == job.Id));
        Assert.False(db.AiComparisonResults.Any(r => r.JobId == job.Id));
        Assert.False(db.AiUsageLogs.Any(l => l.JobId == job.Id));
    }

    [Fact]
    public async Task DeleteCheckAsync_OlmayanKayitIcinSessizceGecer()
    {
        var (svc, _) = CreateService();
        await svc.DeleteCheckAsync(999); // hata fırlatmamalı
    }
}
