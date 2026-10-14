using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;
using MainStore = HakedisOtomasyon.Domain.Entities.Store;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma Evap Temin ve Değişim hakedişi: Kompresör Değişim ile birebir aynı (tek ekran, otomatik şehir içi/dışı servis
/// bedeli, fatura ekli özel kalem). Örnek tablo: 4321 BİNEVLER ÇORUM 34.975,00 TL, 5256 5M ANTARES ANKARA 39.074,93 TL.</summary>
public partial class MaintenanceClaimTests
{
    [Fact]
    public void Evap_KompresorIleAyniKurallar_BaslikVeDosyaAdiTureGore()
    {
        var evap = CoolingClaimCategory.EvapReplacement;
        Assert.True(evap.IsAvailable());
        Assert.True(MaintenanceClaim.IsEvap(MechanicalDiscipline.Cooling, evap));
        Assert.False(MaintenanceClaim.IsEvap(MechanicalDiscipline.Fire, evap));
        Assert.True(MaintenanceClaim.UsesMaterialScreen(MechanicalDiscipline.Cooling, evap));
        Assert.True(MaintenanceClaim.AllowsCustomItems(MechanicalDiscipline.Cooling, evap));
        Assert.Equal("MİGROS SOĞUTMA EVAP TEMİN VE DEĞİŞİM", MaintenanceClaim.TitleCore(evap));
        Assert.Equal("EvapDegisim_Hakedis", MaintenanceClaim.ExportFilePrefix(evap));

        // özel kalem / fatura girişi: Kompresör, Evap, Kısmi Tadilat'ta açık; Bakım & Onarım'da kapalı
        Assert.True(MaintenanceClaim.AllowsCustomItems(MechanicalDiscipline.Cooling, CoolingClaimCategory.CompressorReplacement));
        Assert.True(MaintenanceClaim.AllowsCustomItems(MechanicalDiscipline.Cooling, CoolingClaimCategory.PartialRenovation));
        Assert.False(MaintenanceClaim.AllowsCustomItems(MechanicalDiscipline.Cooling, CoolingClaimCategory.MaintenanceRepair));
        Assert.False(MaintenanceClaim.AllowsCustomItems(MechanicalDiscipline.Cooling, CoolingClaimCategory.GlycolUsage));
    }

    [Fact]
    public async Task Evap_OrnekTablo_SatirlarKodlarVeMagazaToplamlari()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        c.Claim.CoolingCategory = CoolingClaimCategory.EvapReplacement;
        var corum = new MainStore { Code = "4321", Name = "BİNEVLER ÇORUM M MİGROS", Format = "SERVİS REYONLU M", Region = "İÇ ANADOLU", City = "ÇORUM", Discipline = MechanicalDiscipline.Cooling };
        var antares = new MainStore { Code = "5256", Name = "5M ANTARES AVM ANKARA", Format = "5M", Region = "İÇ ANADOLU", City = "ANKARA", Discipline = MechanicalDiscipline.Cooling };
        db.Stores.AddRange(corum, antares);
        var orfis = Price("36", "ORFİS", null, "ADET", eur: 9.2610m);                                             // 9,2610 × 53,29 = 493,52
        var fan = Price("2", "DOLAP ELEKRONİK FAN MOTORU IP 65 ECM 12 - 15 ELCO ,ECR2,ECM7112", "IP 65 ECM 12 - 15 ELCO ,ECR2,ECM7112", "ADET", eur: 26.0709m);   // → 1.389,32
        db.PriceItems.AddRange(orfis, fan);
        await db.SaveChangesAsync();

        // 4321 ÇORUM (şehir dışı)
        var d1 = new DateTime(2026, 4, 3);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, corum.Id, d1, "25684", c.S3, 4m);
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, corum.Id, d1, "25684", new CustomClaimItemInput
        {
            Name = "250 CM ŞARKÜTERİ HİZMET EVAP", Type = "EVAPORATÖR", Basis = CustomPriceBasis.InvoicePlusMarkup,
            BaseAmount = 25250m, Percent = 10m, InvoiceFilePath = "Faturalar/evap-corum.pdf", InvoiceFileName = "evap-corum.pdf",   // +%10 → 27.775,00
        });

        // 5256 ANKARA (şehir içi)
        var d2 = new DateTime(2026, 4, 21);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, antares.Id, d2, "20750", c.S3, 2m);
        await c.Svc.AddCustomMaintenanceItemAsync(c.File.Id, antares.Id, d2, "20750", new CustomClaimItemInput
        { Name = "250 CM ŞARKÜTERİ HİZMET EVAP", Type = "EVAPORATÖR", Basis = CustomPriceBasis.InvoicePlusMarkup, BaseAmount = 27180m, Percent = 10m });   // → 29.898,00
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, antares.Id, d2, "20750", c.Valve, 1m);       // 59,5595 × 53,29 → 3.173,93 (örnekte 3.044,09: eski fiyat)
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, antares.Id, d2, "20750", orfis.Id, 1m);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, antares.Id, d2, "20750", fan.Id, 1m);

        Assert.Equal(2, await db.Invoices.CountAsync());                    // iki faturalı kalem → iki fatura kaydı

        var claim = await LoadClaimAsync(db, c.Claim.Id);
        var ws = BuildSheet(claim, claim.ServiceForms.ToList(), out var wb);
        using (wb)
        {
            static double Disp(IXLCell cell) => (double)Math.Round((decimal)cell.GetDouble(), 2, MidpointRounding.AwayFromZero);

            Assert.Equal("İÇ ANADOLU MİGROS SOĞUTMA EVAP TEMİN VE DEĞİŞİM HAKEDİŞİ", ws.Cell(1, 1).GetString());
            Assert.Equal(new[] { 4321, 4321, 4321, 5256, 5256, 5256, 5256, 5256, 5256 },
                Enumerable.Range(4, 9).Select(r => (int)ws.Cell(r, 2).GetDouble()).ToArray());
            Assert.Equal(new[] { "S2", "S3", "FATURA +%10 M.K.", "S1", "S3", "FATURA +%10 M.K.", "35", "36", "2" },
                Enumerable.Range(4, 9).Select(r => ws.Cell(r, 7).GetString()).ToArray());
            Assert.Equal("250 CM ŞARKÜTERİ HİZMET EVAP", ws.Cell(6, 8).GetString());
            Assert.Equal("EVAPORATÖR", ws.Cell(6, 9).GetString());
            Assert.Equal(27775.00, Disp(ws.Cell(6, 13)));
            Assert.Equal(29898.00, Disp(ws.Cell(9, 13)));
            Assert.Equal(493.52, Disp(ws.Cell(11, 13)));
            Assert.Equal(1389.32, Disp(ws.Cell(12, 13)));

            Assert.Equal(34975.00, Disp(ws.Cell(4, 14)));                  // 4.200 + 3.000 + 27.775 — örnek tablodaki tutar
            // 5256: 2.750 + 1.500 + 29.898 + 3.173,93 + 493,52 + 1.389,32 (örnekte genleşme vanası eski fiyatla 3.044,09 → 39.074,93)
            Assert.Equal(39204.76, Disp(ws.Cell(7, 14)));                  // satırlar yuvarlanmadan toplanır
            Assert.Equal("İÇ ANADOLU MİGROS SOĞUTMA EVAP TEMİN VE DEĞİŞİM İCMALİ", wb.Worksheet(1).Cell(2, 1).GetString());
        }
    }
}
