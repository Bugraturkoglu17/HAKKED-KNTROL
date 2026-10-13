using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma Kısmi Tadilat hakedişi: Bakım &amp; Onarım ile aynı (tek ekran, otomatik şehir içi/dışı servis bedeli); Kompresör
/// Değişim'deki gibi fatura ekli özel kalem girilebilir. Yalnız çıktı başlığı farklıdır.</summary>
public partial class MaintenanceClaimTests
{
    private static async Task<Ctx> PartialRenovationScreenAsync(MainDb db)
    {
        var c = await ScreenAsync(db);
        c.Claim.CoolingCategory = CoolingClaimCategory.PartialRenovation;
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public void KismiTadilat_AyniEkraniKullanir_BaslikVeDosyaAdiTureGore()
    {
        Assert.True(MaintenanceClaim.UsesMaterialScreen(MechanicalDiscipline.Cooling, CoolingClaimCategory.PartialRenovation));
        Assert.True(MaintenanceClaim.IsPartialRenovation(MechanicalDiscipline.Cooling, CoolingClaimCategory.PartialRenovation));
        Assert.False(MaintenanceClaim.IsPartialRenovation(MechanicalDiscipline.Fire, CoolingClaimCategory.PartialRenovation));
        Assert.Equal("MİGROS SATIŞ MAĞAZALARI SOĞUTMA KISMİ TADİLAT", MaintenanceClaim.TitleCore(CoolingClaimCategory.PartialRenovation));
        Assert.Equal("KismiTadilat_Hakedis", MaintenanceClaim.ExportFilePrefix(CoolingClaimCategory.PartialRenovation));
        Assert.Equal("KompresorDegisim_Hakedis", MaintenanceClaim.ExportFilePrefix(CoolingClaimCategory.CompressorReplacement));
        Assert.Equal("BakimOnarim_Hakedis", MaintenanceClaim.ExportFilePrefix(CoolingClaimCategory.MaintenanceRepair));
        // tek ekranı kullanmayan türler
        Assert.False(MaintenanceClaim.UsesMaterialScreen(MechanicalDiscipline.Cooling, CoolingClaimCategory.GlycolUsage));
        Assert.False(MaintenanceClaim.UsesMaterialScreen(MechanicalDiscipline.Cooling, CoolingClaimCategory.GasUsage));
    }

    [Fact]
    public async Task KismiTadilat_ServisBedeliOtomatik_SabitServisUcretiYok()
    {
        using var db = CreateDb();
        var c = await PartialRenovationScreenAsync(db);

        var uploaded = await c.Svc.AddFormAsync(c.Claim.Id, "forms/baska.pdf", "baska.pdf");
        Assert.Empty(uploaded.ServiceItems);

        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, new DateTime(2026, 7, 28), "15541", c.S3, 12m);
        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, r.Fee);
        Assert.Equal(new[] { "S2", "S3" }, r.Form.ServiceItems.OrderBy(i => i.SortOrder).Select(i => i.ItemCode).ToArray());
    }

    /// <summary>Örnek tablonun ilk mağazası: 383 G.O.PAŞA ANKARA — S1 2.750,00 + FATURA +%10 (71.823,18 → 79.005,50) = 81.755,50 TL.</summary>
    [Fact]
    public async Task KismiTadilat_FaturaEkliKalem_OrnekTablodakiTutarlar_ExcelBasligi()
    {
        using var db = CreateDb();
        var c = await PartialRenovationScreenAsync(db);

        var r = await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 2), "20300", new CustomClaimItemInput
        {
            Name = "PVPRO TAMİR BEDELİ", Type = "71.823,18 TL", Quantity = 1m, Unit = "ADET",
            Basis = CustomPriceBasis.InvoicePlusMarkup, BaseAmount = 71823.18m, Percent = 10m,
            InvoiceFilePath = "Faturalar/pvpro.pdf", InvoiceFileName = "pvpro.pdf",
        });

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, r.Fee);
        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal("Faturalar/pvpro.pdf", invoice.FilePath);            // fatura eki forma bağlandı
        Assert.Equal(71823.18m, invoice.InvoiceAmount);

        var claim = await LoadClaimAsync(db, c.Claim.Id);
        var ws = BuildSheet(claim, claim.ServiceForms.ToList(), out var wb);
        using (wb)
        {
            static double Disp(IXLCell cell) => (double)Math.Round((decimal)cell.GetDouble(), 2, MidpointRounding.AwayFromZero);

            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA KISMİ TADİLAT HAKEDİŞİ", ws.Cell(1, 1).GetString());
            Assert.Equal(new[] { "S1", "FATURA +%10 M.K." }, Enumerable.Range(4, 2).Select(row => ws.Cell(row, 7).GetString()).ToArray());
            Assert.Equal("PVPRO TAMİR BEDELİ", ws.Cell(5, 8).GetString());
            Assert.Equal("71.823,18 TL", ws.Cell(5, 9).GetString());
            Assert.Equal(79005.50, Disp(ws.Cell(5, 12)));
            Assert.Equal(79005.50, Disp(ws.Cell(5, 13)));
            Assert.Equal(81755.50, Disp(ws.Cell(4, 14)));
            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA KISMİ TADİLAT HAKEDİŞİ", ws.Cell(7, 1).GetString());
            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA KISMİ TADİLAT İCMALİ", wb.Worksheet(1).Cell(2, 1).GetString());
        }
    }

    /// <summary>Fiyat listesinde olmayan elektrik malzemesi gibi kalemlerde kullanıcı kendi malzeme kodunu yazabilir.</summary>
    [Fact]
    public async Task OzelKalem_KullaniciKodGirerse_KodSutunundaOYazar_GirmezseFiyatTuruEtiketi()
    {
        using var db = CreateDb();
        var c = await PartialRenovationScreenAsync(db);
        var date = new DateTime(2026, 7, 15);

        var withCode = await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, date, "21254", new CustomClaimItemInput
        { Name = "2x0.75 LIHCH HFFR Kablo", Code = "  ELK.MLZ. 3.6 ", Quantity = 100m, Unit = "metre", Basis = CustomPriceBasis.ListPrice, BaseAmount = 39.9528m });
        var withoutCode = await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, date, "21254", new CustomClaimItemInput
        { Name = "PLASTİK SPİRAL BORULAR Q20", Quantity = 30m, Unit = "Metre", Basis = CustomPriceBasis.ListPrice, BaseAmount = 24.4973m });

        var a = await db.ServiceItems.SingleAsync(i => i.Id == withCode.ItemId);
        Assert.Equal("ELK.MLZ. 3.6", a.ItemCode);
        Assert.Equal("metre", a.Unit);
        Assert.Equal(3995.28m, a.TotalPrice);                              // örnek tabloda 3.995,28
        var b = await db.ServiceItems.SingleAsync(i => i.Id == withoutCode.ItemId);
        Assert.Equal("LİSTE FİYATI", b.ItemCode);
        Assert.Equal(734.92m, b.TotalPrice);                               // örnek tabloda 734,92
    }
}
