using HakedisOtomasyon.Application;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;
using MainClaim = HakedisOtomasyon.Domain.Entities.ProgressClaim;
using MainForm = HakedisOtomasyon.Domain.Entities.ServiceForm;
using MainPriceItem = HakedisOtomasyon.Domain.Entities.PriceItem;
using MainStore = HakedisOtomasyon.Domain.Entities.Store;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Bakım &amp; Onarım tek ekranı: yüklenen dosyadaki servis formlarına alt alta kalem ekleme (servis katmanı).</summary>
public partial class MaintenanceClaimTests
{
    private sealed record Ctx(MainDb Db, ServiceFormService Svc, MainClaim Claim, MainForm File, MainStore Ankara, MainStore Eskisehir,
        int S1, int S2, int S3, int Valve);

    private static MainPriceItem Price(string code, string desc, string? type, string unit, decimal tl = 0m, decimal? eur = null) => new()
    {
        PozNo = code, Description = desc, ItemType = type, Unit = unit, MaterialPrice = tl, ForeignPrice = eur, CurrencyCode = eur.HasValue ? "EUR" : null,
        IsCurrencyBased = eur.HasValue, Discipline = MechanicalDiscipline.Cooling, IsActive = true,
    };

    private static async Task<Ctx> ScreenAsync(MainDb db, decimal? eurRate = Rate)
    {
        var (svc, claim) = await SeedAsync(db);
        claim.EurRate = eurRate; claim.EurRateSource = "Manuel"; claim.EurRateDate = new DateTime(2026, 8, 1);
        var ankara = new MainStore { Code = "386", Name = "BAHÇELİEVLER ANKARA MM MİGROS", Format = "MM", Region = "İÇ ANADOLU", City = "ANKARA", Discipline = MechanicalDiscipline.Cooling };
        var eskisehir = new MainStore { Code = "3484", Name = "ESPARK ESKİŞEHİR MMM MİGROS", Format = "MMM", Region = "İÇ ANADOLU", City = "ESKİŞEHİR", Discipline = MechanicalDiscipline.Cooling };
        db.Stores.AddRange(ankara, eskisehir);
        var s1 = Price("S1", "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ 1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", null, "set", 2750m);
        var s2 = Price("S2", "1 EKIP ŞEHİR DIŞI SERVİS BEDELİ 1 EKIP ŞEHİR DIŞI SERVİS BEDELİ", null, "set", 4200m);
        var s3 = Price("S3", "ADAM SAAT GUNDUZ /GECE (>=2. GUN) ADAM SAAT GUNDUZ /GECE (>=2. GUN)", null, "set", 750m);
        var valve = Price("35", "EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", "DIŞ DENGELİ VE KAYNAKLI", "ADET", eur: 59.5595m);
        db.PriceItems.AddRange(s1, s2, s3, valve);
        var file = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "temmuz.pdf", FilePath = "forms/temmuz.pdf" };
        db.ServiceForms.Add(file);
        await db.SaveChangesAsync();
        return new Ctx(db, svc, claim, file, ankara, eskisehir, s1.Id, s2.Id, s3.Id, valve.Id);
    }

    [Fact]
    public async Task IlkKalemEklenince_FormKaydiOlusurVeSehirIciServisBedeliOtomatikGelir()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);

        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24, 9, 0, 0), " 21179 ", c.S3, 8m);

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, r.Fee);
        Assert.False(r.Merged);
        Assert.Equal(c.File.Id, r.Form.Id);                              // yüklenen dosyanın boş kaydı kullanıldı
        Assert.Equal(ServiceFormStatus.Tamamlandi, r.Form.Status);
        Assert.Equal("386", r.Form.StoreCode);
        Assert.Equal(new DateTime(2026, 7, 24), r.Form.FormDate);
        Assert.Equal("21179", r.Form.FormNumber);

        var items = await db.ServiceItems.Where(i => i.ServiceFormId == r.Form.Id).OrderBy(i => i.SortOrder).ToListAsync();
        Assert.Equal(new[] { "S1", "S3" }, items.Select(i => i.ItemCode).ToArray());   // servis bedeli İLK kalem
        Assert.Equal("1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", items[0].Description);
        Assert.Equal(1m, items[0].Quantity);
        Assert.Equal(2750m, items[0].TotalPrice);
        Assert.Equal(r.ItemId, items[1].Id);
        Assert.Equal("ADAM SAAT GUNDUZ /GECE (>=2. GUN)", items[1].ExportDescription);
        Assert.Equal(8m, items[1].Quantity);
        Assert.Equal(6000m, items[1].TotalPrice);
        Assert.Equal("set", items[1].Unit);
    }

    [Fact]
    public async Task AyniFormaKalemEklenmeyeDevamEdilir_BedelTekrarEklenmez_EurKalemKurlaFiyatlanir()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        var date = new DateTime(2026, 7, 24);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, date, "21179", c.S3, 8m);

        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, date, "21179", c.Valve, 2m);

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Unchanged, r.Fee);
        Assert.Single(await db.ServiceForms.ToListAsync());                 // aynı form kaydı
        var items = await db.ServiceItems.OrderBy(i => i.SortOrder).ToListAsync();
        Assert.Equal(new[] { "S1", "S3", "35" }, items.Select(i => i.ItemCode).ToArray());
        var valve = items[2];
        Assert.Equal("EXPANSİON VALVE", valve.ExportDescription);
        Assert.Equal("DIŞ DENGELİ VE KAYNAKLI", valve.ItemType);
        Assert.Equal("ADET", valve.Unit);
        Assert.Equal("EUR", valve.CurrencyCode);
        Assert.Equal(Rate, valve.ExchangeRate);
        Assert.Equal(3173.93m, valve.UnitPrice);          // 59,5595 × 53,29 (kuruşa yuvarlı gösterim)
        Assert.Equal(6347.85m, valve.TotalPrice);         // 2 × 59,5595 × 53,29 = 6.347,85 (örnek tablodaki değer)
    }

    [Fact]
    public async Task AyniKalemIkinciKezEklenince_SatirCogalmazMiktarArtar()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        var date = new DateTime(2026, 7, 24);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, date, "21179", c.Valve, 2m);

        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, date, "21179", c.Valve, 1m);

        Assert.True(r.Merged);
        var valve = Assert.Single(await db.ServiceItems.Where(i => i.ItemCode == "35").ToListAsync());
        Assert.Equal(3m, valve.Quantity);
        Assert.Equal(EurPricing.TotalTl(3m, 59.5595m, Rate), valve.TotalPrice);
    }

    [Fact]
    public async Task AyniDosyadanBaskaMagazaVeyaFormNo_AyriFormKaydiOlur_AnkaraDisiSehirDisiBedelAlir()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.S3, 8m);

        var other = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, new DateTime(2026, 7, 2), "21050", c.Valve, 1m);
        var sameStoreOtherForm = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 25), "21183", null, 0m);

        var forms = await db.ServiceForms.Include(f => f.ServiceItems).ToListAsync();
        Assert.Equal(3, forms.Count);
        Assert.All(forms, f => Assert.Equal("forms/temmuz.pdf", f.FilePath));   // dosya kopyalanmaz, kayıtlar aynı dosyaya bağlı
        var otherForm = forms.Single(f => f.Id == other.Form.Id);
        Assert.Equal(new[] { "S2", "35" }, otherForm.ServiceItems.OrderBy(i => i.SortOrder).Select(i => i.ItemCode).ToArray());
        Assert.Equal(4200m, otherForm.ServiceItems.Single(i => i.ItemCode == "S2").TotalPrice);
        // malzeme seçilmeden: yalnız form + servis bedeli
        Assert.Null(sameStoreOtherForm.ItemId);
        Assert.Equal("S1", Assert.Single(forms.Single(f => f.Id == sameStoreOtherForm.Form.Id).ServiceItems).ItemCode);
    }

    [Fact]
    public async Task OtomatikBedelinKendisiSecilirse_IkiKatinaCikmaz()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);

        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.S1, 1m);

        var fee = Assert.Single(await db.ServiceItems.ToListAsync());
        Assert.Equal(1m, fee.Quantity);
        Assert.Equal(2750m, fee.TotalPrice);
        Assert.True(r.Merged);
    }

    [Fact]
    public async Task MagazaninIliKayitliDegilse_BedelEklenmezAmaKalemEklenir()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        c.Ankara.City = null;
        await db.SaveChangesAsync();

        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.S3, 2m);

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.UnknownCity, r.Fee);
        Assert.Equal("S3", Assert.Single(await db.ServiceItems.ToListAsync()).ItemCode);
    }

    [Fact]
    public async Task EurKuruYokken_EurKalemEklenemez_TlKalemEklenir()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db, eurRate: null);
        var date = new DateTime(2026, 7, 24);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, date, "21179", c.Valve, 1m));
        Assert.Contains("EUR kuru", ex.Message);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, date, "21179", c.S3, 1m);
        Assert.DoesNotContain(await db.ServiceItems.ToListAsync(), i => i.ItemCode == "35");
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("21179", 0)]
    [InlineData("21179", -2)]
    public async Task EksikFormNoVeyaGecersizMiktar_Reddedilir(string formNo, int qty)
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), formNo, c.S3, qty));
        Assert.Empty(await db.ServiceItems.ToListAsync());
    }

    [Fact]
    public async Task GlikolHakedisinde_BakimOnarimIslemiReddedilir()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        c.Claim.CoolingCategory = CoolingClaimCategory.GlycolUsage;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "1", c.S3, 1m));
    }

    [Fact]
    public async Task Guncelleme_MiktarVeFormBilgisiDegisir_MagazaAnkaraDisinaTasininca_BedelSehirDisiOlur()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        var r = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.S3, 8m);

        var dto = await c.Svc.UpdateMaintenanceEntryAsync(r.Form.Id, c.Eskisehir.Id, new DateTime(2026, 7, 26), "21180", r.ItemId, 5m);

        Assert.Equal("3484", dto.StoreCode);
        Assert.Equal(new DateTime(2026, 7, 26), dto.FormDate);
        Assert.Equal("21180", dto.FormNumber);
        var items = await db.ServiceItems.OrderBy(i => i.SortOrder).ToListAsync();
        Assert.Equal(new[] { "S2", "S3" }, items.Select(i => i.ItemCode).ToArray());
        Assert.Equal(4200m, items[0].TotalPrice);
        Assert.Equal(5m, items[1].Quantity);
        Assert.Equal(3750m, items[1].TotalPrice);
    }

    [Fact]
    public async Task Guncelleme_BaskaKayitlaAyniMagazaTarihFormNoOlamaz()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.S3, 8m);
        var second = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 25), "21183", c.S3, 1m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Svc.UpdateMaintenanceEntryAsync(second.Form.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", null, null));
    }

    [Fact]
    public async Task KalemSilme_SonKalemSilinince_FormKaydiKalkar_DosyaninSonKaydiysaBosalir()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        var a = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.S3, 8m);
        var b = await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, new DateTime(2026, 7, 2), "21050", null, 0m);

        // kalem silinir, servis bedeli kaldığı için form durur
        var afterItem = await c.Svc.RemoveMaintenanceItemAsync(a.ItemId!.Value);
        Assert.NotNull(afterItem);
        Assert.Equal("S1", Assert.Single(afterItem!.ServiceItems).ItemCode);

        // ikinci formun tek kalemi (bedel) silinir → başka kayıt olduğundan form kaydı tamamen kalkar
        var feeB = await db.ServiceItems.SingleAsync(i => i.ServiceFormId == b.Form.Id);
        Assert.Null(await c.Svc.RemoveMaintenanceItemAsync(feeB.Id));
        Assert.False(await db.ServiceForms.AnyAsync(f => f.Id == b.Form.Id));

        // dosyanın son kaydının son kalemi silinir → kayıt boş yer tutucuya döner, dosya hakedişte kalır
        var feeA = await db.ServiceItems.SingleAsync(i => i.ServiceFormId == a.Form.Id);
        var last = await c.Svc.RemoveMaintenanceItemAsync(feeA.Id);
        Assert.NotNull(last);
        Assert.Equal(ServiceFormStatus.Bekliyor, last!.Status);
        Assert.Equal(string.Empty, last.StoreCode);
        Assert.Null(last.FormNumber);
        Assert.Equal("forms/temmuz.pdf", (await db.ServiceForms.SingleAsync()).FilePath);
    }

    /// <summary>Tek ekrandan girilen kayıtların Excel'i: mağaza kodu → tarih sırası; formda servis bedeli başta, kalemler eklenme sırasıyla.</summary>
    [Fact]
    public async Task TekEkrandanGirilenKayitlar_ExceldeBeklenenSiradaCikar()
    {
        using var db = CreateDb();
        var c = await ScreenAsync(db);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Eskisehir.Id, new DateTime(2026, 7, 2), "21050", c.Valve, 1m);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.S3, 8m);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 23), "21169", null, 0m);
        await c.Svc.AddMaintenanceItemAsync(c.File.Id, c.Ankara.Id, new DateTime(2026, 7, 24), "21179", c.Valve, 2m);

        var claim = await db.ProgressClaims.Include(x => x.ServiceForms).ThenInclude(f => f.Store)
            .Include(x => x.ServiceForms).ThenInclude(f => f.ServiceItems).FirstAsync(x => x.Id == c.Claim.Id);
        var ws = BuildSheet(claim, claim.ServiceForms.ToList(), out var wb);
        using (wb)
        {
            // 386: 23 Tem (S1) → 24 Tem (S1, S3, 35) ; sonra 3484: S2, 35
            Assert.Equal(new[] { "S1", "S1", "S3", "35", "S2", "35" }, Enumerable.Range(4, 6).Select(r => ws.Cell(r, 7).GetString()).ToArray());
            Assert.Equal(new[] { 386, 386, 386, 386, 3484, 3484 }, Enumerable.Range(4, 6).Select(r => (int)ws.Cell(r, 2).GetDouble()).ToArray());
            Assert.Equal("EXPANSİON VALVE", ws.Cell(7, 8).GetString());
            Assert.Equal("DIŞ DENGELİ VE KAYNAKLI", ws.Cell(7, 9).GetString());
            Near(2750 + 2750 + 6000 + 2 * 59.5595 * 53.29, ws.Cell(4, 14).GetDouble());
        }
    }
}
