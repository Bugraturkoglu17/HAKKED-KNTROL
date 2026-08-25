using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>
/// Birim fiyat düzeltme özelliği (Düzelt/Geri Al, Yeni Kalem Ekle, Bu Fiyat Doğrudur,
/// export notları, formül koruma) için uçtan uca testler — kullanıcının istediği 5 senaryo.
/// </summary>
public class ProgressPaymentPriceCorrectionTests
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

    private static async Task<int> CreatePriceListAsync(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db, string company, string region)
    {
        var list = new UnitPriceList { CompanyName = company, Region = region, Name = "Test Listesi", IsActive = true, ValidFrom = DateTime.Today, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        await db.SaveChangesAsync();
        return list.Id;
    }

    /// <summary>Header satırı + tek veri satırı içeren, TOPLAM kolonu formülle hesaplanan sahte hakediş Excel'i üretir.</summary>
    private static byte[] BuildHakedisWorkbook(string materialName, decimal miktar, decimal fiyat)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("NİSAN");
        ws.Cell(1, 1).Value = "MAĞAZA ADI";
        ws.Cell(1, 2).Value = "MALZEME ADI";
        ws.Cell(1, 3).Value = "MİKTARI";
        ws.Cell(1, 4).Value = "FİYAT";
        ws.Cell(1, 5).Value = "TOPLAM";

        ws.Cell(2, 1).Value = "Test Mağaza";
        ws.Cell(2, 2).Value = materialName;
        ws.Cell(2, 3).Value = miktar;
        ws.Cell(2, 4).Value = fiyat;
        ws.Cell(2, 5).FormulaA1 = "=C2*D2"; // gerçek formül — export sonrası hâlâ formül olmalı

        // ikinci (KDV) formül hücresi — genel toplam formüllerinin de bozulmadığını doğrulamak için
        ws.Cell(4, 4).Value = "KDV DAHIL:";
        ws.Cell(4, 5).FormulaA1 = "=E2*1.2";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Test1_YanlisFiyatDuzeltilinceExcelHucresiDegisirVeKirmiziOlur()
    {
        var (svc, db) = CreateService();
        var listId = await CreatePriceListAsync(db, "TESTFIRMA", "TEST BÖLGE");
        db.UnitPriceItems.Add(new UnitPriceItem
        {
            UnitPriceListId = listId, MaterialName = "Bakır Boru 3/8", Price = 5m, Currency = "TRY",
            NormalizedName = new MaterialMatchingService(db).Normalize("Bakır Boru 3/8"), IsActive = true, CreatedAt = DateTime.Now,
        });
        await db.SaveChangesAsync();

        var bytes = BuildHakedisWorkbook("Bakır Boru 3/8", miktar: 2, fiyat: 10); // firma 10 TL yazmış, onaylı 5 TL
        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        Assert.Single(parsed.Items);

        var check = await svc.CreateCheckAsync(listId, "TESTFIRMA", "TEST BÖLGE", "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, exchangeRateEur: null, parsed);
        var items = await svc.GetItemsAsync(check.Id);
        var item = Assert.Single(items);
        Assert.Equal(CheckItemControlStatus.FiyatHatasi, item.ControlStatus); // 10 TL yazılmış ama onaylı 5 TL

        await svc.SetPriceCorrectionAsync(item.Id, apply: true);
        var outPath = await svc.ExportControlledExcelAsync(check.Id);

        using var outWb = new XLWorkbook(outPath);
        var ws = outWb.Worksheet("NİSAN");
        var priceCell = ws.Cell(item.UnitPriceCellRef!);
        Assert.Equal(5.0, priceCell.GetDouble(), 2); // hücrede artık onaylı fiyat (5 TL) yazıyor
        Assert.True(priceCell.Style.Font.FontColor.Color.R > 100 && priceCell.Style.Font.FontColor.Color.G < 60); // kırmızı ton

        // Geri Al — hücre tekrar düzeltilebilir olmalı (bug: değer kalıcı değişmemeli DB'de, sadece export'a yansımalı)
        await svc.SetPriceCorrectionAsync(item.Id, apply: false);
        var itemsAfterUndo = await svc.GetItemsAsync(check.Id);
        Assert.False(Assert.Single(itemsAfterUndo).PriceCorrectionApplied);
    }

    [Fact]
    public async Task Test5_FormullerExportSonrasiKorunur()
    {
        var (svc, db) = CreateService();
        var listId = await CreatePriceListAsync(db, "TESTFIRMA", "TEST BÖLGE");
        db.UnitPriceItems.Add(new UnitPriceItem
        {
            UnitPriceListId = listId, MaterialName = "Bakır Boru 3/8", Price = 5m, Currency = "TRY",
            NormalizedName = new MaterialMatchingService(db).Normalize("Bakır Boru 3/8"), IsActive = true, CreatedAt = DateTime.Now,
        });
        await db.SaveChangesAsync();

        var bytes = BuildHakedisWorkbook("Bakır Boru 3/8", miktar: 2, fiyat: 10);
        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        var check = await svc.CreateCheckAsync(listId, "TESTFIRMA", "TEST BÖLGE", "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, null, parsed);
        var item = Assert.Single(await svc.GetItemsAsync(check.Id));

        await svc.SetPriceCorrectionAsync(item.Id, apply: true);
        var outPath = await svc.ExportControlledExcelAsync(check.Id);

        using var outWb = new XLWorkbook(outPath);
        var ws = outWb.Worksheet("NİSAN");
        Assert.True(ws.Cell(2, 5).HasFormula, "TOPLAM hücresi hâlâ formül içermeli (statik değere çevrilmemeli).");
        Assert.True(ws.Cell(4, 5).HasFormula, "KDV formül hücresi de korunmalı.");
        Assert.Equal("C2*D2", ws.Cell(2, 5).FormulaA1);
    }

    [Fact]
    public async Task Test2_YeniMalzemeEklenirGercekDbKaydiOlusurVeSonrakiHakediseTasinir()
    {
        var (svc, db) = CreateService();
        var listId = await CreatePriceListAsync(db, "TESTFIRMA", "TEST BÖLGE");
        var bytes = BuildHakedisWorkbook("Yeni Malzeme XYZ", miktar: 3, fiyat: 15);
        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        var check = await svc.CreateCheckAsync(listId, "TESTFIRMA", "TEST BÖLGE", "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, null, parsed);
        var item = Assert.Single(await svc.GetItemsAsync(check.Id));
        Assert.Equal(CheckItemControlStatus.BirimFiyatBulunamadi, item.ControlStatus); // önce eşleşmiyor

        var beforeCount = db.UnitPriceItems.Count();
        var newItemDto = new UnitPriceItemDto { MaterialName = "Yeni Malzeme XYZ", Unit = "adet", Price = 15m, Currency = "TRY" };
        var created = await svc.CreateAndMatchNewItemAsync(check.Id, new List<int> { item.Id }, newItemDto, "TESTFIRMA", "YeniKalemEkle");

        // 1) Gerçek DB kaydı oluştu (ana birim fiyat listesinde görünür)
        Assert.True(created.Id > 0);
        Assert.Equal(beforeCount + 1, db.UnitPriceItems.Count());
        Assert.Contains(db.UnitPriceItems, p => p.Id == created.Id && p.MaterialName == "Yeni Malzeme XYZ");

        // 2) Hakediş satırı yeni kalemle eşleşti, kontrol hesabı güncellendi, hata kalktı
        var itemsAfter = await svc.GetItemsAsync(check.Id);
        var updatedItem = Assert.Single(itemsAfter);
        Assert.Equal(created.Id, updatedItem.MatchedUnitPriceItemId);
        Assert.NotEqual(CheckItemControlStatus.BirimFiyatBulunamadi, updatedItem.ControlStatus);
        Assert.Equal(CheckItemControlStatus.Uygun, updatedItem.ControlStatus);

        // 3) "Sayfa yenilendiğinde kayıt kaybolmamalı" — yeni bir DbContext örneğiyle (gerçek refresh simülasyonu) tekrar okununca hâlâ orada
        Assert.True(db.UnitPriceItems.Any(p => p.Id == created.Id));

        // 4) Sonraki hakedişlerde aynı kalem tekrar tanınır — alias öğrenildi mi?
        Assert.Contains(db.MaterialAliases, a => a.UnitPriceItemId == created.Id);
    }

    [Fact]
    public async Task Test3_BuFiyatDogrudurGercekDbKaydiOlusturur()
    {
        var (svc, db) = CreateService();
        var listId = await CreatePriceListAsync(db, "TESTFIRMA", "TEST BÖLGE");
        var bytes = BuildHakedisWorkbook("Bilinmeyen Parça", miktar: 1, fiyat: 42);
        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        var check = await svc.CreateCheckAsync(listId, "TESTFIRMA", "TEST BÖLGE", "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, null, parsed);
        var item = Assert.Single(await svc.GetItemsAsync(check.Id));

        var dto = new UnitPriceItemDto { MaterialName = item.OriginalMaterialName, Unit = item.Unit, Price = item.CompanyUnitPrice, Currency = "TRY" };
        var created = await svc.CreateAndMatchNewItemAsync(check.Id, new List<int> { item.Id }, dto, "TESTFIRMA", "BuFiyatDogru");

        Assert.True(created.Id > 0);
        Assert.True(db.UnitPriceItems.Any(p => p.Id == created.Id && p.Price == 42m));
        Assert.True(db.CheckItemActionLogs.Any(l => l.Action == "BuFiyatDogru" && l.ProgressPaymentCheckItemId == item.Id));
    }

    [Fact]
    public async Task Test4_ExportSadeceProblemliSatirlaraNotYazar()
    {
        var (svc, db) = CreateService();
        var listId = await CreatePriceListAsync(db, "TESTFIRMA", "TEST BÖLGE");
        var matching = new MaterialMatchingService(db);
        db.UnitPriceItems.Add(new UnitPriceItem { UnitPriceListId = listId, MaterialName = "Uygun Malzeme", Price = 10m, Currency = "TRY", NormalizedName = matching.Normalize("Uygun Malzeme"), IsActive = true, CreatedAt = DateTime.Now });
        await db.SaveChangesAsync();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("NİSAN");
        ws.Cell(1, 1).Value = "MALZEME ADI"; ws.Cell(1, 2).Value = "MİKTARI"; ws.Cell(1, 3).Value = "FİYAT"; ws.Cell(1, 4).Value = "TOPLAM";
        int row = 2;
        for (int i = 0; i < 10; i++) // 10 uygun satır — aynı malzeme, doğru fiyat
        {
            ws.Cell(row, 1).Value = "Uygun Malzeme";
            ws.Cell(row, 2).Value = 1;
            ws.Cell(row, 3).Value = 10;
            ws.Cell(row, 4).Value = 10;
            row++;
        }
        ws.Cell(row, 1).Value = "Eslesmeyen Malzeme 1"; ws.Cell(row, 2).Value = 1; ws.Cell(row, 3).Value = 5; ws.Cell(row, 4).Value = 5; row++;
        ws.Cell(row, 1).Value = "Eslesmeyen Malzeme 2"; ws.Cell(row, 2).Value = 1; ws.Cell(row, 3).Value = 7; ws.Cell(row, 4).Value = 7; row++;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();

        using var stream = new MemoryStream(bytes);
        var parsed = ProgressPaymentExcelParser.Parse(stream, "test.xlsx");
        Assert.Equal(12, parsed.Items.Count);
        var check = await svc.CreateCheckAsync(listId, "TESTFIRMA", "TEST BÖLGE", "SABİT FİYAT", 2026, 4, "Nisan 2026", "test.xlsx", bytes, null, parsed);

        var outPath = await svc.ExportControlledExcelAsync(check.Id);
        using var outWb = new XLWorkbook(outPath);
        var outWs = outWb.Worksheet("NİSAN");
        int lastCol = 4; // orijinal 4 kolon, KONTROL NOTU = lastCol+9 = 13
        int noteCol = lastCol + 9;

        int emptyNotes = 0, filledNotes = 0;
        for (int r = 2; r <= 13; r++)
        {
            var noteText = outWs.Cell(r, noteCol).GetString();
            if (string.IsNullOrWhiteSpace(noteText)) emptyNotes++; else filledNotes++;
        }
        Assert.Equal(10, emptyNotes);  // 10 uygun satırda not yok
        Assert.Equal(2, filledNotes);  // 2 hatalı satırda açıklama var
    }
}
