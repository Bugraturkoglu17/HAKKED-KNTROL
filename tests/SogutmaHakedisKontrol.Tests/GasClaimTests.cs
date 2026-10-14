using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.ExcelExport;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;
using MainClaim = HakedisOtomasyon.Domain.Entities.ProgressClaim;
using MainForm = HakedisOtomasyon.Domain.Entities.ServiceForm;
using MainItem = HakedisOtomasyon.Domain.Entities.ServiceItem;
using MainPriceItem = HakedisOtomasyon.Domain.Entities.PriceItem;
using MainStore = HakedisOtomasyon.Domain.Entities.Store;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma Gaz Kullanım hakedişi: Glikol ile AYNI (servis ücreti yok, form başına tek satır, aynı Excel düzeni).
/// Tek fark: kalem otomatik gelmez — satırın gaz türü (R22, R404, R407C, R410A, R134a) seçilir.</summary>
public class GasClaimTests
{
    private const decimal Rate = 53m;

    private static MainDb CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gas_test_{Guid.NewGuid():N}.db");
        var db = new MainDb(new DbContextOptionsBuilder<MainDb>().UseSqlite($"Data Source={path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed record Ctx(ServiceFormService Svc, MainClaim Claim, MainForm File, MainStore Store, MainStore Other, int R404, int R134a, int Glycol, int Valve);

    private static MainPriceItem Price(string code, string desc, decimal eur) => new()
    {
        PozNo = code, Description = desc, Unit = "kg", ForeignPrice = eur, CurrencyCode = "EUR", IsCurrencyBased = true,
        Discipline = MechanicalDiscipline.Cooling, IsActive = true,
    };

    private static async Task<Ctx> SeedAsync(MainDb db, CoolingClaimCategory category = CoolingClaimCategory.GasUsage, decimal? rate = Rate)
    {
        var r404 = Price("167", "SOĞUTUCU GAZ 10,9 KG TÜP (kg fiyatı) R404", 13m);
        var r134a = Price("170", "SOĞUTUCU GAZ 13,6 KG TÜP (kg fiyatı) R134a", 14m);
        var glycol = Price("171", "İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL GLİKOL", 8.5m);
        var valve = Price("35", "EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", 59.5595m);
        db.PriceItems.AddRange(r404, r134a, glycol, valve);
        var store = new MainStore { Code = "1761", Name = "GÖKSUPARK MMM MİGROS", Format = "MMM", Region = "İÇ ANADOLU", City = "ANKARA", Discipline = MechanicalDiscipline.Cooling };
        var other = new MainStore { Code = "3239", Name = "BARTIN MMM MİGROS", Format = "MMM", Region = "İÇ ANADOLU", City = "BARTIN", Discipline = MechanicalDiscipline.Cooling };
        db.Stores.AddRange(store, other);
        var claim = new MainClaim
        {
            Name = "Temmuz Gaz", CompanyName = "İNTİKOŞ", Month = 7, Year = 2026, Discipline = MechanicalDiscipline.Cooling,
            CoolingCategory = category, EurRate = rate, EurRateSource = "Manuel", EurRateDate = new DateTime(2026, 8, 1),
        };
        db.ProgressClaims.Add(claim);
        await db.SaveChangesAsync();
        var file = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "gaz.pdf", FilePath = "forms/gaz.pdf" };
        db.ServiceForms.Add(file);
        await db.SaveChangesAsync();
        return new Ctx(new ServiceFormService(db, new FakeFileStorage(), null!, null!), claim, file, store, other, r404.Id, r134a.Id, glycol.Id, valve.Id);
    }

    // ---------------------------------------------------------------- tür
    [Fact]
    public void GazKullanim_GlikolIleAyniEkran_BaslikTureGore()
    {
        Assert.True(GlycolClaim.IsGas(MechanicalDiscipline.Cooling, CoolingClaimCategory.GasUsage));
        Assert.False(GlycolClaim.IsGas(MechanicalDiscipline.Fire, CoolingClaimCategory.GasUsage));
        Assert.True(GlycolClaim.IsUsageClaim(MechanicalDiscipline.Cooling, CoolingClaimCategory.GasUsage));
        Assert.True(GlycolClaim.IsUsageClaim(MechanicalDiscipline.Cooling, CoolingClaimCategory.GlycolUsage));
        Assert.False(GlycolClaim.IsUsageClaim(MechanicalDiscipline.Cooling, CoolingClaimCategory.MaintenanceRepair));
        Assert.True(CoolingClaimCategory.GasUsage.IsAvailable());

        Assert.Equal("MİGROS SATIŞ MAĞAZALARI SOĞUTMA GAZ KULLANIM", GlycolClaim.TitleCore(CoolingClaimCategory.GasUsage));
        Assert.Equal("MİGROS SATIŞ MAĞAZALARI SOĞUTMA GLİKOL KULLANIM", GlycolClaim.TitleCore(CoolingClaimCategory.GlycolUsage));

        Assert.True(GlycolClaim.IsGasPriceName("SOĞUTUCU GAZ 10,9 KG TÜP (kg fiyatı) R404"));
        Assert.True(GlycolClaim.IsGasPriceName("  soğutucu gaz 13,5 kg tüp r22"));
        Assert.False(GlycolClaim.IsGasPriceName("GAZ GÖSTERGESİ 1/4\"KAYNAKLI"));      // gaz göstergesi gaz değildir
        Assert.False(GlycolClaim.IsGasPriceName("AZOT GAZI 1 m3"));
        Assert.False(GlycolClaim.IsGasPriceName("İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL"));
    }

    // ---------------------------------------------------------------- servis
    [Fact]
    public async Task FormEklenince_ServisUcretiYok_KalemOtomatikEklenmez()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);

        var form = await c.Svc.AddFormAsync(c.Claim.Id, "forms/x.pdf", "x.pdf");
        Assert.Empty(form.ServiceItems);

        // eski akıştan kalma servis ücreti temizlenir; gaz kalemi otomatik EKLENMEZ (tür satırda seçilir)
        db.ServiceItems.Add(new MainItem { ServiceFormId = c.File.Id, Description = "Servis Ücreti", UnitPrice = 4550m, TotalPrice = 4550m });
        await db.SaveChangesAsync();
        await c.Svc.EnsureGlycolItemsAsync(c.Claim.Id);
        Assert.Empty(await db.ServiceItems.ToListAsync());
    }

    [Fact]
    public async Task SatirEklenince_SecilenGazKalemiKoduAdiVeFiyatiylaYazilir()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);

        var dto = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), " 21156 ", 15m, c.R404);

        Assert.Equal(c.File.Id, dto.Id);                                   // yüklenen dosyanın boş kaydı kullanıldı
        Assert.Equal(ServiceFormStatus.Tamamlandi, dto.Status);
        Assert.Equal("21156", dto.FormNumber);
        var item = Assert.Single(await db.ServiceItems.ToListAsync());      // form başına TEK kalem, servis ücreti yok
        Assert.Equal("167", item.ItemCode);
        Assert.Equal("SOĞUTUCU GAZ 10,9 KG TÜP (kg fiyatı) R404", item.Description);
        Assert.Equal("kg", item.Unit);
        Assert.Equal(15m, item.Quantity);
        Assert.Equal("EUR", item.CurrencyCode);
        Assert.Equal(13m, item.ForeignPrice);
        Assert.Equal(Rate, item.ExchangeRate);
        Assert.Equal(689m, item.UnitPrice);                                 // 13 × 53
        Assert.Equal(10335m, item.TotalPrice);                              // 15 × 689
    }

    /// <summary>Çok sayfalı PDF: kayıt eklendiği sayfayı tutar; düzenlemede (sayfa verilmeden) sayfa değişmez.</summary>
    [Fact]
    public async Task CokSayfaliPdf_KaydinSayfasiSaklanir_DuzenlemedeDegismez()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);

        var p3 = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "21156", 15m, c.R404, pageNumber: 3);
        var p7 = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Other.Id, new DateTime(2026, 7, 22), "21200", 10m, c.R404, pageNumber: 7);
        Assert.Equal(3, p3.PageNumber);
        Assert.Equal(7, p7.PageNumber);

        var edited = await c.Svc.SaveGlycolRowAsync(p3.Id, c.Store.Id, new DateTime(2026, 7, 21), "21157", 16m, c.R404);
        Assert.Equal(3, edited.PageNumber);
    }

    [Fact]
    public async Task AyniDosyadanFarkliGazlarVeMagazalar_AyriSatirlarOlur()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);
        var date = new DateTime(2026, 7, 20);

        var a = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, date, "21156", 15m, c.R404);
        var b = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, date, "21156", 4m, c.R134a);     // aynı formda ikinci gaz
        var d = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Other.Id, new DateTime(2026, 7, 22), "21200", 10m, c.R404);

        Assert.Equal(3, new[] { a.Id, b.Id, d.Id }.Distinct().Count());
        var forms = await db.ServiceForms.Include(f => f.ServiceItems).ToListAsync();
        Assert.All(forms, f => Assert.Equal("forms/gaz.pdf", f.FilePath));
        Assert.All(forms, f => Assert.Single(f.ServiceItems));
        Assert.Equal(new[] { "167", "167", "170" }, forms.Select(f => f.ServiceItems.Single().ItemCode).OrderBy(x => x).ToArray());
        Assert.Equal(4m * 14m * Rate, forms.Single(f => f.Id == b.Id).ServiceItems.Single().TotalPrice);
    }

    [Fact]
    public async Task GazTuruSecilmezse_VeyaGazOlmayanKalemSecilirse_Reddedilir()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);
        var date = new DateTime(2026, 7, 20);

        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, date, "21156", 15m));
        Assert.Contains("Gaz türünü seçin", none.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, date, "21156", 15m, c.Valve));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, date, "21156", 15m, c.Glycol));

        Assert.Empty(await db.ServiceItems.ToListAsync());
        Assert.Null((await db.ServiceForms.SingleAsync()).StoreId);         // boş form kaydı kirlenmedi
    }

    [Fact]
    public async Task Guncelleme_GazTuruDegisebilir_SecilmezseMevcutTurKorunur()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);
        var row = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "21156", 15m, c.R404);

        // tür verilmeden: miktar/form no değişir, gaz türü aynı kalır
        await c.Svc.SaveGlycolRowAsync(row.Id, c.Store.Id, new DateTime(2026, 7, 21), "21157", 20m);
        var item = await db.ServiceItems.SingleAsync();
        Assert.Equal("167", item.ItemCode);
        Assert.Equal(20m, item.Quantity);
        Assert.Equal(20m * 13m * Rate, item.TotalPrice);

        // tür değiştirilir: kod, ad ve fiyat yeni gaza göre
        await c.Svc.SaveGlycolRowAsync(row.Id, c.Store.Id, new DateTime(2026, 7, 21), "21157", 20m, c.R134a);
        db.ChangeTracker.Clear();
        item = await db.ServiceItems.SingleAsync();                         // satır çoğalmadı
        Assert.Equal("170", item.ItemCode);
        Assert.EndsWith("R134a", item.Description);
        Assert.Equal(20m * 14m * Rate, item.TotalPrice);
    }

    [Fact]
    public async Task SatirSilme_SonKayitsaBosalirKalemKalkar_BaskaKayitVarsaSatirKalkar()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);
        var a = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "21156", 15m, c.R404);
        var b = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Other.Id, new DateTime(2026, 7, 22), "21200", 10m, c.R134a);

        Assert.Null(await c.Svc.RemoveGlycolRowAsync(b.Id));                // başka kayıt var → satır tamamen kalktı
        var last = await c.Svc.RemoveGlycolRowAsync(a.Id);                  // dosyanın son kaydı → boşalır, dosya kalır
        Assert.NotNull(last);
        Assert.Equal(ServiceFormStatus.Bekliyor, last!.Status);
        Assert.Empty(await db.ServiceItems.ToListAsync());                  // gaz kalemi de kalktı (tür yeniden seçilir)
        Assert.Equal("forms/gaz.pdf", (await db.ServiceForms.SingleAsync()).FilePath);

        // boşalan kayıt yeniden kullanılabilir
        var again = await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 25), "21300", 5m, c.R134a);
        Assert.Equal(a.Id, again.Id);
        Assert.Equal("170", (await db.ServiceItems.SingleAsync()).ItemCode);
    }

    [Fact]
    public async Task EurKuruYokken_GazSatiriEklenemez()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db, rate: null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "21156", 15m, c.R404));
        Assert.Contains("EUR kuru", ex.Message);
    }

    /// <summary>Glikol hakedişi etkilenmedi: kalem hâlâ otomatik, verilen gaz kimliği yok sayılır.</summary>
    [Fact]
    public async Task GlikolHakedisinde_KalemOtomatikGlikoldur_GazSecimiYokSayilir()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db, CoolingClaimCategory.GlycolUsage);
        await c.Svc.EnsureGlycolItemsAsync(c.Claim.Id);

        await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "21156", 30m, c.R404);

        var item = Assert.Single(await db.ServiceItems.ToListAsync());
        Assert.Equal("171", item.ItemCode);
        Assert.Equal("İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL", item.Description);
        Assert.Equal(30m * 8.5m * Rate, item.TotalPrice);
    }

    [Fact]
    public async Task BakimOnarimHakedisinde_KullanimIslemleriReddedilir()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db, CoolingClaimCategory.MaintenanceRepair);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "1", 1m, c.R404));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.EnsureGlycolItemsAsync(c.Claim.Id));
    }

    // ---------------------------------------------------------------- Excel (Glikol ile aynı düzen)
    [Fact]
    public async Task Excel_GlikolIleAyniDuzen_BaslikGaz_SatirdaGazKoduVeAdi_ToplamlarDogru()
    {
        using var db = CreateDb();
        var c = await SeedAsync(db);
        // BİLEREK karışık sırada
        await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Other.Id, new DateTime(2026, 7, 22), "21200", 10m, c.R404);
        await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "21156", 15m, c.R404);
        await c.Svc.AddGlycolEntryAsync(c.File.Id, c.Store.Id, new DateTime(2026, 7, 20), "21157", 4m, c.R134a);
        var claim = await db.ProgressClaims.Include(x => x.ServiceForms).ThenInclude(f => f.Store)
            .Include(x => x.ServiceForms).ThenInclude(f => f.ServiceItems).FirstAsync(x => x.Id == c.Claim.Id);

        using var wb = new XLWorkbook();
        var ws = GlycolClaimExcelBuilder.Build(wb, claim, claim.ServiceForms);
        wb.RecalculateAllFormulas();

        // Glikol'deki sütunların aynısı
        Assert.Equal(new[]
        {
            "MAĞAZA KODU", "MAĞAZA ADI", "FORMAT", "TARİH", "FORM NO", "MALZEME KODU", "MALZEME ADI",
            "ONAYA SUNULAN MİKTAR", "KESİLEN MİKTAR", "FATURA DÖNEMİ", "FİYAT", "TOPLAM", "GENEL TOPLAM",
        }, Enumerable.Range(1, 13).Select(col => ws.Cell(1, col).GetString()).ToArray());
        Assert.Equal("MALZ HAKEDIS", ws.Name);
        Assert.Equal("GENEL ICMAL", wb.Worksheet(1).Name);

        // mağaza koduna göre: 1761 (iki form) → 3239
        Assert.Equal(new[] { 1761, 1761, 3239 }, Enumerable.Range(2, 3).Select(r => (int)ws.Cell(r, 1).GetDouble()).ToArray());
        Assert.Equal(new[] { 167, 170, 167 }, Enumerable.Range(2, 3).Select(r => (int)ws.Cell(r, 6).GetDouble()).ToArray());
        Assert.Equal("SOĞUTUCU GAZ 10,9 KG TÜP (kg fiyatı) R404", ws.Cell(2, 7).GetString());
        Assert.Equal("SOĞUTUCU GAZ 13,6 KG TÜP (kg fiyatı) R134a", ws.Cell(3, 7).GetString());
        Assert.Equal("TEMMUZ", ws.Cell(2, 10).GetString());
        Assert.True(ws.Cell(2, 9).IsEmpty());                                // KESİLEN MİKTAR boş

        Assert.Equal(689.0, ws.Cell(2, 11).GetDouble(), 6);                  // 13 × 53
        Assert.Equal(742.0, ws.Cell(3, 11).GetDouble(), 6);                  // 14 × 53
        Assert.Equal(10335.0, ws.Cell(2, 12).GetDouble(), 6);
        Assert.Equal(2968.0, ws.Cell(3, 12).GetDouble(), 6);
        Assert.Equal(13303.0, ws.Cell(2, 13).GetDouble(), 6);                // 1761 mağaza toplamı (iki satır birleşik)
        Assert.Equal(6890.0, ws.Cell(4, 13).GetDouble(), 6);
        Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA GAZ KULLANIM HAKEDİŞİ", ws.Cell(5, 1).GetString());
        Assert.Equal(20193.0, ws.Cell(5, 13).GetDouble(), 6);
        Assert.Contains("Birim fiyatlar = EUR fiyatı × EUR kuru 53", ws.Cell(7, 1).GetString());   // farklı gaz fiyatları → genel not

        var icmal = wb.Worksheet(1);
        Assert.Equal("Temmuz 26", icmal.Cell(1, 1).GetString());
        Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA GAZ KULLANIM İCMALİ", icmal.Cell(2, 1).GetString());
        Assert.Equal(20193.0, icmal.Cell(7, 6).GetDouble(), 6);

        // kesilen miktar Glikol'deki gibi doğrudan GENEL TOPLAM'dan düşer
        ws.Cell(3, 9).Value = 1;
        wb.RecalculateAllFormulas();
        Assert.Equal(13303.0 - 742.0, ws.Cell(2, 13).GetDouble(), 6);
    }
}
