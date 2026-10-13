using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.ExcelExport;
using HakedisOtomasyon.Infrastructure.PdfProcessing;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;
using MainStore = HakedisOtomasyon.Domain.Entities.Store;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma Kompresör Değişim hakedişi: Bakım &amp; Onarım ile aynı yapı + hakedişe özel kalem (liste fiyatı, iskontolu liste
/// fiyatı, FATURA + %). Örnek tablo: 4 mağaza, 14 satır, genel toplam 397.671,43 TL.</summary>
public partial class MaintenanceClaimTests
{
    // ---------------------------------------------------------------- özel kalemin fiyatı ve kodu
    [Fact]
    public void OzelKalem_FaturaArtiYuzde_FiyatVeKodEtiketi()
    {
        var (price, code) = MaintenanceClaim.CustomItemPricing(CustomPriceBasis.InvoicePlusMarkup, 100000m, 10m);
        Assert.Equal(110000m, price);
        Assert.Equal("FATURA +%10 M.K.", code);

        // yüzde elle girilir — sabit %10 değil
        var other = MaintenanceClaim.CustomItemPricing(CustomPriceBasis.InvoicePlusMarkup, 100000m, 12.5m);
        Assert.Equal(112500m, other.UnitPrice);
        Assert.Equal("FATURA +%12,5 M.K.", other.CodeLabel);
    }

    [Fact]
    public void OzelKalem_ListeFiyatiVeIskontoluListeFiyati()
    {
        Assert.Equal((29682.53m, "LİSTE FİYATI"), MaintenanceClaim.CustomItemPricing(CustomPriceBasis.ListPrice, 29682.53m, 99m));   // yüzde yok sayılır

        var discounted = MaintenanceClaim.CustomItemPricing(CustomPriceBasis.DiscountedListPrice, 17225.06m, 10m);
        Assert.Equal(15502.554m, discounted.UnitPrice);                 // örnek tabloda 15.502,55
        Assert.Equal("%10 İSKONTOLU LİSTE FİYATI", discounted.CodeLabel);
    }

    [Theory]
    [InlineData(CustomPriceBasis.InvoicePlusMarkup, 0, 10)]
    [InlineData(CustomPriceBasis.InvoicePlusMarkup, -5, 10)]
    [InlineData(CustomPriceBasis.InvoicePlusMarkup, 100, -1)]
    [InlineData(CustomPriceBasis.DiscountedListPrice, 100, 101)]
    public void OzelKalem_GecersizTutarVeyaYuzde_Reddedilir(CustomPriceBasis basis, int amount, int percent)
        => Assert.Throws<InvalidOperationException>(() => MaintenanceClaim.CustomItemPricing(basis, amount, percent));

    // ---------------------------------------------------------------- servis (veritabanı)
    private static async Task<Ctx> CompressorScreenAsync(MainDb db)
    {
        var c = await ScreenAsync(db);
        c.Claim.CoolingCategory = CoolingClaimCategory.CompressorReplacement;
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task KompresorHakedisinde_AyniEkran_ServisBedeliOtomatik_SabitServisUcretiYok()
    {
        using var db = CreateDb();
        var c = await CompressorScreenAsync(db);
        Assert.True(MaintenanceClaim.UsesMaterialScreen(c.Claim.Discipline, c.Claim.CoolingCategory));

        var uploaded = await c.Svc.AddFormAsync(c.Claim.Id, "forms/baska.pdf", "baska.pdf");
        Assert.Empty(uploaded.ServiceItems);                                 // sabit "Servis Ücreti" eklenmez

        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, new DateTime(2026, 7, 8), "20334", c.S3, 35m);
        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, r.Fee);
        Assert.Equal(new[] { "S2", "S3" }, r.Form.ServiceItems.OrderBy(i => i.SortOrder).Select(i => i.ItemCode).ToArray());
    }

    [Fact]
    public async Task FaturaliOzelKalem_HakediseEklenir_FaturaKaydiBaglanir_FiyatListesineEklenmez()
    {
        using var db = CreateDb();
        var c = await CompressorScreenAsync(db);
        var priceCount = await db.PriceItems.CountAsync();

        var r = await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, new DateTime(2026, 7, 8), "20334", new CustomClaimItemInput
        {
            Name = "  BİTZER 4J-13-2Y-40P VE   4NCS-12.2Y-40P ", Type = "2 ADET KOMPRESÖR REVİZYONU", Quantity = 1m, Unit = "ADET",
            Basis = CustomPriceBasis.InvoicePlusMarkup, BaseAmount = 101279.53m, Percent = 10m,
            InvoiceFilePath = "Faturalar/bitzer.pdf", InvoiceFileName = "bitzer.pdf",
        });

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, r.Fee);        // form ilk kez oluştu → şehir dışı bedeli otomatik
        var items = await db.ServiceItems.Where(i => i.ServiceFormId == r.Form.Id).OrderBy(i => i.SortOrder).ToListAsync();
        Assert.Equal(new[] { "S2", "FATURA +%10 M.K." }, items.Select(i => i.ItemCode).ToArray());
        var item = items[1];
        Assert.Equal(r.ItemId, item.Id);
        Assert.Equal("BİTZER 4J-13-2Y-40P VE 4NCS-12.2Y-40P", item.ExportDescription);
        Assert.Equal("2 ADET KOMPRESÖR REVİZYONU", item.ItemType);
        Assert.Equal(111407.483m, item.UnitPrice);                           // 101.279,53 × 1,10
        Assert.Equal(111407.48m, item.TotalPrice);                           // örnek tablodaki tutar
        Assert.True(item.IsInvoiceBased);
        Assert.True(item.IsManualEntry);

        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal(invoice.Id, item.InvoiceId);
        Assert.Equal(r.Form.Id, invoice.ServiceFormId);
        Assert.Equal("Faturalar/bitzer.pdf", invoice.FilePath);
        Assert.Equal(101279.53m, invoice.InvoiceAmount);
        Assert.Equal(0.10m, invoice.MarkupRate);

        Assert.Equal(priceCount, await db.PriceItems.CountAsync());          // genel fiyat listesine EKLENMEDİ

        // ekrana dönen kayıtta fatura da var (satırdaki ataç simgesi için)
        var dtoItem = r.Form.ServiceItems.Single(i => i.Id == item.Id);
        Assert.True(dtoItem.IsInvoiceBased);
        Assert.Equal("bitzer.pdf", r.Form.Invoices.Single(i => i.Id == dtoItem.InvoiceId).FileName);

        // hakediş toplamında fatura tutarı İKİ KEZ sayılmaz (kalem + fatura)
        var claims = new ProgressClaimService(db, new CurrentDisciplineService { CurrentDiscipline = MechanicalDiscipline.Cooling });
        Assert.Equal(4200m + 111407.48m, (await claims.GetByIdAsync(c.Claim.Id))!.TotalAmount);
    }

    [Fact]
    public async Task ListeFiyatliOzelKalem_FaturaKaydiOlusturmaz()
    {
        using var db = CreateDb();
        var c = await CompressorScreenAsync(db);

        var r = await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21177", new CustomClaimItemInput
        { Name = "PANASONİC CSBN 303 H8G", Type = "SIFIR KOMPRESÖR", Basis = CustomPriceBasis.ListPrice, BaseAmount = 29682.53m });

        var item = await db.ServiceItems.SingleAsync(i => i.Id == r.ItemId);
        Assert.Equal("LİSTE FİYATI", item.ItemCode);
        Assert.Equal(29682.53m, item.TotalPrice);
        Assert.False(item.IsInvoiceBased);
        Assert.Empty(await db.Invoices.ToListAsync());
    }

    [Theory]
    [InlineData("", 1, 100)]
    [InlineData("KOMPRESÖR", 0, 100)]
    [InlineData("KOMPRESÖR", 1, 0)]
    public async Task OzelKalem_EksikAdMiktarVeyaTutar_ReddedilirVeBosFormBirakmaz(string name, int qty, int amount)
    {
        using var db = CreateDb();
        var c = await CompressorScreenAsync(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Ankara.Id,
            new DateTime(2026, 7, 24), "21177", new CustomClaimItemInput { Name = name, Quantity = qty, BaseAmount = amount, Percent = 10m }));

        Assert.Empty(await db.ServiceItems.ToListAsync());                   // servis bedeli bile eklenmedi
        Assert.Null((await db.ServiceForms.SingleAsync()).StoreId);
    }

    [Fact]
    public async Task FaturaliKalemSilinince_FaturaKaydiDaKalkar()
    {
        using var db = CreateDb();
        var c = await CompressorScreenAsync(db);
        var r = await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21177", new CustomClaimItemInput
        { Name = "KOMPRESÖR", Basis = CustomPriceBasis.InvoicePlusMarkup, BaseAmount = 1000m, Percent = 10m, InvoiceFilePath = "Faturalar/x.pdf", InvoiceFileName = "x.pdf" });
        Assert.Single(await db.Invoices.ToListAsync());

        var after = await c.Svc.RemoveMaintenanceItemAsync(r.ItemId!.Value);

        Assert.Empty(await db.Invoices.ToListAsync());
        Assert.Equal("S1", Assert.Single(after!.ServiceItems).ItemCode);     // servis bedeli kaldığı için form durur
        Assert.Empty(after.Invoices);
    }

    // ---------------------------------------------------------------- Excel: örnek tablo (397.671,43 TL)
    private static async Task<(Ctx c, MainStore aksaray, MainStore afyon, MainStore parkSite)> SampleAsync(MainDb db)
    {
        var c = await CompressorScreenAsync(db);
        MainStore S(string code, string name, string format, string city) =>
            new() { Code = code, Name = name, Format = format, Region = "İÇ ANADOLU", City = city, Discipline = MechanicalDiscipline.Cooling };
        var aksaray = S("2477", "AKSARAY MM MİGROS", "MM", "AKSARAY");
        var afyon = S("2890", "5M AFYON", "5M", "AFYONKARAHİSAR");
        var parkSite = S("7699", "PARK SİTE AKSARAY MM MİGROS", "MM", "AKSARAY");
        var kecioren = S("5281", "AKSARAY CAD KEÇİÖREN ANKARA M", "SERVİS REYONLU M", "ANKARA");
        db.Stores.AddRange(aksaray, afyon, parkSite, kecioren);
        var oil = Price("173", "KOMPRESÖR YAĞI ESTER 68", "ESTER 68", "kg", eur: 20m);                  // 20 × 53,29 = 1.065,80
        var solenoid = Price("27", "SOLENOİD GÖVDE 3/8\" KAYNAKLI", "3/8\" KAYNAKLI", "ADET", eur: 25.2m); // 25,2 × 53,29 = 1.342,91
        db.PriceItems.AddRange(oil, solenoid);
        await db.SaveChangesAsync();

        // BİLEREK karışık sırada girilir
        var d4 = new DateTime(2026, 7, 21);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, parkSite.Id, d4, "36454", c.S3, 16m);
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, parkSite.Id, d4, "36454", new CustomClaimItemInput
        { Name = "COPELAND SCROLL ZBD45 + ZB38 KCE TFD 551", Type = "SIFIR KOMPRESÖR", Basis = CustomPriceBasis.ListPrice, BaseAmount = 170741.16m });
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, parkSite.Id, d4, "36454", oil.Id, 6m);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, parkSite.Id, d4, "36454", solenoid.Id, 1m);

        var d1 = new DateTime(2026, 7, 8);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, aksaray.Id, d1, "20334", c.S3, 35m);
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, aksaray.Id, d1, "20334", new CustomClaimItemInput
        {
            Name = "BİTZER 4J-13-2Y-40P VE 4NCS-12.2Y-40P", Type = "2 ADET KOMPRESÖR REVİZYONU",
            Basis = CustomPriceBasis.InvoicePlusMarkup, BaseAmount = 101279.5273m, Percent = 10m,   // +%10 → 111.407,48
        });

        var d3 = new DateTime(2026, 7, 24);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, kecioren.Id, d3, "21177", c.S3, 10m);
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, kecioren.Id, d3, "21177", new CustomClaimItemInput
        { Name = "PANASONİC CSBN 303 H8G", Type = "SIFIR KOMPRESÖR", Basis = CustomPriceBasis.ListPrice, BaseAmount = 29682.53m });

        var d2 = new DateTime(2026, 7, 4);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, afyon.Id, d2, "20429", c.S3, 2m);
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, afyon.Id, d2, "20429", new CustomClaimItemInput
        { Name = "TECUMSEH", Type = "SIFIR KOMPRESÖR", Basis = CustomPriceBasis.DiscountedListPrice, BaseAmount = 17225.06m, Percent = 10m });   // → 15.502,55
        return (c, aksaray, afyon, parkSite);
    }

    private static async Task<HakedisOtomasyon.Domain.Entities.ProgressClaim> LoadClaimAsync(MainDb db, int claimId)
        => await db.ProgressClaims
            .Include(x => x.ServiceForms).ThenInclude(f => f.Store)
            .Include(x => x.ServiceForms).ThenInclude(f => f.ServiceItems)
            .Include(x => x.ServiceForms).ThenInclude(f => f.Invoices)
            .FirstAsync(x => x.Id == claimId);

    [Fact]
    public async Task Excel_KompresorOrnekTablosu_SatirlarKodlarVeToplamlarAyni()
    {
        using var db = CreateDb();
        var (c, _, _, _) = await SampleAsync(db);
        var claim = await LoadClaimAsync(db, c.Claim.Id);

        var ws = BuildSheet(claim, claim.ServiceForms.ToList(), out var wb);
        using (wb)
        {
            static double Disp(IXLCell cell) => (double)Math.Round((decimal)cell.GetDouble(), 2, MidpointRounding.AwayFromZero);

            Assert.Equal("İÇ ANADOLU MİGROS SOĞUTMA KOMPRESÖR TEMİN VE DEĞİŞİM HAKEDİŞİ", ws.Cell(1, 1).GetString());
            // 14 satır: mağaza kodu sırasıyla; her formda önce otomatik servis bedeli (Ankara S1, diğer iller S2)
            Assert.Equal(new[] { 2477, 2477, 2477, 2890, 2890, 2890, 5281, 5281, 5281, 7699, 7699, 7699, 7699, 7699 },
                Enumerable.Range(4, 14).Select(r => (int)ws.Cell(r, 2).GetDouble()).ToArray());
            Assert.Equal(new[]
            {
                "S2", "S3", "FATURA +%10 M.K.",
                "S2", "S3", "%10 İSKONTOLU LİSTE FİYATI",
                "S1", "S3", "LİSTE FİYATI",
                "S2", "S3", "LİSTE FİYATI", "173", "27",
            }, Enumerable.Range(4, 14).Select(r => ws.Cell(r, 7).GetString()).ToArray());

            Assert.Equal("BİTZER 4J-13-2Y-40P VE 4NCS-12.2Y-40P", ws.Cell(6, 8).GetString());
            Assert.Equal("2 ADET KOMPRESÖR REVİZYONU", ws.Cell(6, 9).GetString());
            Assert.Equal("TECUMSEH", ws.Cell(9, 8).GetString());
            Assert.Equal("SIFIR KOMPRESÖR", ws.Cell(9, 9).GetString());
            Assert.Equal("KOMPRESÖR YAĞI", ws.Cell(16, 8).GetString());
            Assert.Equal("ESTER 68", ws.Cell(16, 9).GetString());
            Assert.Equal("kg", ws.Cell(16, 11).GetString());

            // FİYAT / TOPLAM — örnek tablodaki rakamlar
            Assert.Equal(111407.48, Disp(ws.Cell(6, 13)));
            Assert.Equal(15502.55, Disp(ws.Cell(9, 13)));
            Assert.Equal(29682.53, Disp(ws.Cell(12, 13)));
            Assert.Equal(170741.16, Disp(ws.Cell(15, 13)));
            Assert.Equal(1065.80, Disp(ws.Cell(16, 12)));
            Assert.Equal(6394.80, Disp(ws.Cell(16, 13)));
            Assert.Equal(1342.91, Disp(ws.Cell(17, 13)));
            Assert.Equal(26250.00, Disp(ws.Cell(5, 13)));

            // GENEL TOPLAM (mağaza blokları) ve alt toplam
            Assert.Equal(141857.48, Disp(ws.Cell(4, 14)));
            Assert.Equal(21202.55, Disp(ws.Cell(7, 14)));
            Assert.Equal(39932.53, Disp(ws.Cell(10, 14)));
            Assert.Equal(194678.87, Disp(ws.Cell(13, 14)));
            Assert.Equal("İÇ ANADOLU MİGROS SOĞUTMA KOMPRESÖR TEMİN VE DEĞİŞİM HAKEDİŞİ", ws.Cell(19, 1).GetString());
            Assert.Equal(397671.43, Disp(ws.Cell(19, 14)));

            var icmal = wb.Worksheet(1);
            Assert.Equal("İÇ ANADOLU MİGROS SOĞUTMA KOMPRESÖR TEMİN VE DEĞİŞİM İCMALİ", icmal.Cell(2, 1).GetString());
            Assert.Equal("AFYONKARAHİSAR", icmal.Cell(5, 4).GetString());
            Assert.Equal(397671.43, Disp(icmal.Cell(9, 6)));
        }
    }

    /// <summary>Bakım &amp; Onarım başlığı Kompresör eklenince değişmedi.</summary>
    [Fact]
    public void Baslik_HakedisTurunegore()
    {
        Assert.Equal("MİGROS SATIŞ MAĞAZALARI SOĞUTMA SERVİS VE BAKIM MALZEMELERİ", MaintenanceClaim.TitleCore(CoolingClaimCategory.MaintenanceRepair));
        Assert.Equal("MİGROS SOĞUTMA KOMPRESÖR TEMİN VE DEĞİŞİM", MaintenanceClaim.TitleCore(CoolingClaimCategory.CompressorReplacement));
    }

    // ---------------------------------------------------------------- Excel: faturalar tablonun sağında
    private static string CreateInvoiceImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fatura_{Guid.NewGuid():N}.png");
        using var bitmap = new SKBitmap(600, 800);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            canvas.DrawRect(20, 20, 560, 760, paint);
            for (int y = 120; y < 700; y += 60) canvas.DrawLine(60, y, 540, y, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public async Task Excel_Faturalar_TablonunSaginaDizilir_YazdirmaAlaniTabloylaSinirli()
    {
        using var db = CreateDb();
        var c = await CompressorScreenAsync(db);
        var image = CreateInvoiceImage();
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, new DateTime(2026, 7, 8), "20334", new CustomClaimItemInput
        { Name = "BİTZER 4J-13-2Y-40P", Type = "KOMPRESÖR REVİZYONU", Basis = CustomPriceBasis.InvoicePlusMarkup, BaseAmount = 100000m, Percent = 10m, InvoiceFilePath = image, InvoiceFileName = "bitzer-fatura.png" });
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21177", new CustomClaimItemInput
        { Name = "TECUMSEH", Basis = CustomPriceBasis.InvoicePlusMarkup, BaseAmount = 5000m, Percent = 15m });   // dosyasız fatura
        var claim = await LoadClaimAsync(db, c.Claim.Id);
        var forms = claim.ServiceForms.ToList();

        using var wb = new XLWorkbook();
        var ws = MaintenanceClaimExcelBuilder.Build(wb, claim, forms);
        var export = new ExcelExportService(db, null!, new FakeFileStorage(), new PdfPreviewService(NullLogger<PdfPreviewService>.Instance),
            NullLogger<ExcelExportService>.Instance, null!, null!, null!, new CurrentDisciplineService { CurrentDiscipline = MechanicalDiscipline.Cooling }, null!);
        await export.AddCoolingInvoiceBlocksAsync(ws, forms, MaintenanceClaimExcelBuilder.LastTableColumn);

        // P sütunu (16): tablo N'de (14) biter, O boşluk
        Assert.Equal("FATURALAR", ws.Cell(1, 16).GetString());
        Assert.StartsWith("Fatura #1 — 386 ", ws.Cell(3, 16).GetString());        // mağaza koduna göre: 386 önce
        Assert.StartsWith("Fatura #2 — 3484 ", ws.Cell(3, 23).GetString());       // 6 sütunluk blok + 1 boşluk
        var text = string.Join("|", ws.Range(3, 16, 12, 28).CellsUsed().Select(x => x.GetString()));
        Assert.Contains("21177", text);
        Assert.Contains("5.000,00 TL", text);
        Assert.Contains("%15", text);
        Assert.Contains("5.750,00 TL", text);                                      // hakedişe yazılan: 5.000 + %15
        Assert.Contains("(fatura dosyası eklenmedi)", text);
        Assert.Contains("bitzer-fatura.png", text);
        Assert.Contains("110.000,00 TL", text);

        Assert.Single(ws.Pictures);                                                // yalnız dosyası olan fatura görsel olarak eklendi
        Assert.Equal(23, ws.Pictures.Single().TopLeftCell.Address.ColumnNumber);
        Assert.Equal("A1:N" + ws.PageSetup.PrintAreas.Single().RangeAddress.LastAddress.RowNumber, ws.PageSetup.PrintAreas.Single().RangeAddress.ToString());

        wb.SaveAs(Path.Combine(Path.GetTempPath(), "kompresor_fatura_test.xlsx"));  // gerçek Excel'de gözle doğrulama için
    }

    [Fact]
    public async Task Excel_FaturaYoksa_SagaHicbirSeyEklenmez()
    {
        using var db = CreateDb();
        var c = await CompressorScreenAsync(db);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21177", c.S3, 2m);
        var claim = await LoadClaimAsync(db, c.Claim.Id);
        var forms = claim.ServiceForms.ToList();

        using var wb = new XLWorkbook();
        var ws = MaintenanceClaimExcelBuilder.Build(wb, claim, forms);
        var export = new ExcelExportService(db, null!, new FakeFileStorage(), new PdfPreviewService(NullLogger<PdfPreviewService>.Instance),
            NullLogger<ExcelExportService>.Instance, null!, null!, null!, new CurrentDisciplineService(), null!);
        await export.AddCoolingInvoiceBlocksAsync(ws, forms, MaintenanceClaimExcelBuilder.LastTableColumn);

        Assert.True(ws.Cell(1, 16).IsEmpty());
        Assert.Empty(ws.Pictures);
    }
}
