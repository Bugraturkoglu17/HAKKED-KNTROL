using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Kullanıcı talebi: "servis içi ve dışı olduğunu sadece Excele bakıp Fiyat Kontrolünde
/// gerçekleştirmek gerekiyor. AI analizi ile bu kısmı formdan kontrol etmiyoruz." — şehir içi/şehir dışı
/// servis bedeli TÜRÜNÜN doğruluğu artık AdditionalWorkComparisonStrategy'de (AI/Form Kontrolü) değil,
/// yalnızca burada (ProgressPaymentCheckService.RecalculateAsync, salt Excel verisiyle) doğrulanır.</summary>
public class ProgressPaymentCheckServiceCityValidationTests
{
    private static (AppDbContext db, ProgressPaymentCheckService svc, ProgressPaymentCheck check, UnitPriceItem sehirIci, UnitPriceItem sehirDisi) Seed()
    {
        var db = TestDbFactory.Create();
        var list = new UnitPriceList { CompanyName = "İNTİKOŞ", Region = "İÇ ANADOLU", Name = "Test Liste", IsActive = true, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        db.SaveChanges();

        var sehirIci = new UnitPriceItem
        {
            UnitPriceListId = list.Id, MaterialName = "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", Spec = null,
            Unit = "set", Price = 2750m, Currency = "TRY", NormalizedName = "1 ekip sehir ici servis bedeli",
            IsActive = true, CreatedAt = DateTime.Now,
        };
        var sehirDisi = new UnitPriceItem
        {
            UnitPriceListId = list.Id, MaterialName = "1 EKIP ŞEHİR DIŞI SERVİS BEDELİ", Spec = null,
            Unit = "set", Price = 4200m, Currency = "TRY", NormalizedName = "1 ekip sehir disi servis bedeli",
            IsActive = true, CreatedAt = DateTime.Now,
        };
        db.UnitPriceItems.AddRange(sehirIci, sehirDisi);

        var check = new ProgressPaymentCheck
        {
            UnitPriceListId = list.Id, CompanyName = "İNTİKOŞ", Region = "İÇ ANADOLU",
            ClaimTypeName = "İLAVE İŞLER", Category = HakedisCategory.AdditionalWork, Year = 2026, Month = 4, PeriodLabel = "Nisan 2026",
            OriginalFileName = "test.xlsx", OriginalFilePath = "test.xlsx",
            Status = ProgressPaymentCheckStatus.Taslak, CreatedAt = DateTime.Now,
        };
        db.ProgressPaymentChecks.Add(check);
        db.SaveChanges();

        var matching = new MaterialMatchingService(db);
        var unitPriceList = new UnitPriceListService(db, matching);
        var appPath = new FakeAppPathService();
        var svc = new ProgressPaymentCheckService(db, matching, appPath, unitPriceList);
        return (db, svc, check, sehirIci, sehirDisi);
    }

    private static ProgressPaymentCheckItem FeeItem(int checkId, int matchedUnitPriceItemId, decimal price, string feeCode, string? storeCity) => new()
    {
        ProgressPaymentCheckId = checkId, StoreCode = "9001", StoreName = "Test Mağaza", StoreCity = storeCity,
        VisitDate = new DateTime(2026, 4, 5), MaintenanceFormNo = "15001",
        IsServiceItem = true, OriginalItemCode = feeCode,
        OriginalMaterialName = feeCode == "S2" ? "1 EKIP ŞEHİR DIŞI SERVİS BEDELİ " : "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ ",
        Quantity = 1, Unit = "set", CompanyUnitPrice = price, CompanyLineTotal = price,
        MatchedUnitPriceItemId = matchedUnitPriceItemId, MatchStatus = MaterialMatchStatus.Exact, MatchConfidence = 1.0m,
        CreatedAt = DateTime.Now,
    };

    [Fact]
    public async Task AnkaraDisindakiMagazayaSehirIciTalepEdilmisse_FiyatHatasiUretir()
    {
        var (db, svc, check, sehirIci, _) = Seed();
        var item = FeeItem(check.Id, sehirIci.Id, 2750m, "S1", "KONYA"); // yanlış: Konya'ya şehir içi yazılmış
        db.ProgressPaymentCheckItems.Add(item);
        db.SaveChanges();

        await svc.RecalculateAsync(check.Id);

        var refreshed = db.ProgressPaymentCheckItems.Single(i => i.Id == item.Id);
        Assert.Equal(CheckItemControlStatus.FiyatHatasi, refreshed.ControlStatus);
        Assert.Contains("KONYA", refreshed.ControlNote);
        Assert.Contains("şehir dışı", refreshed.ControlNote);
    }

    [Fact]
    public async Task AnkaradakiMagazayaSehirDisiTalepEdilmisse_FiyatHatasiUretir()
    {
        var (db, svc, check, _, sehirDisi) = Seed();
        var item = FeeItem(check.Id, sehirDisi.Id, 4200m, "S2", "ANKARA"); // yanlış: Ankara'ya şehir dışı yazılmış
        db.ProgressPaymentCheckItems.Add(item);
        db.SaveChanges();

        await svc.RecalculateAsync(check.Id);

        var refreshed = db.ProgressPaymentCheckItems.Single(i => i.Id == item.Id);
        Assert.Equal(CheckItemControlStatus.FiyatHatasi, refreshed.ControlStatus);
        Assert.Contains("ANKARA", refreshed.ControlNote);
        Assert.Contains("şehir içi", refreshed.ControlNote);
    }

    [Fact]
    public async Task DogruSehirTuruTalepEdilmisse_UygunKalir()
    {
        var (db, svc, check, _, sehirDisi) = Seed();
        var item = FeeItem(check.Id, sehirDisi.Id, 4200m, "S2", "ZONGULDAK"); // doğru: Ankara dışına şehir dışı
        db.ProgressPaymentCheckItems.Add(item);
        db.SaveChanges();

        await svc.RecalculateAsync(check.Id);

        var refreshed = db.ProgressPaymentCheckItems.Single(i => i.Id == item.Id);
        Assert.Equal(CheckItemControlStatus.Uygun, refreshed.ControlStatus);
    }

    [Fact]
    public async Task MagazaIliBulunamazsa_UygunKalirAmaNotDuserBilgilendirir()
    {
        var (db, svc, check, sehirIci, _) = Seed();
        var item = FeeItem(check.Id, sehirIci.Id, 2750m, "S1", storeCity: null);
        db.ProgressPaymentCheckItems.Add(item);
        db.SaveChanges();

        await svc.RecalculateAsync(check.Id);

        var refreshed = db.ProgressPaymentCheckItems.Single(i => i.Id == item.Id);
        // Fiyat kendi içinde tutarlı olduğu için otomatik hata sayılmaz (tahmin edilmez) ama not düşülür.
        Assert.Equal(CheckItemControlStatus.Uygun, refreshed.ControlStatus);
        Assert.Contains("bulunamadı", refreshed.ControlNote);
    }
}
