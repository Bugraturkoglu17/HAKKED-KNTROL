using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Bir malzeme eşleşmesi tek bir kalemde onaylandığında, aynı kontrol içindeki BİREBİR aynı
/// ham ad/spec'e sahip diğer bekleyen (FuzzyPending) kalemlerin de otomatik eşleşmesi gerektiğini
/// doğrular — kullanıcı isteği: "bir malzemeyi eşleştirdiğim zaman o malzemeden Excel'de kaç defa
/// kullanılmışsa otomatik eşleştirilmelidir" (bkz. ProgressPaymentCheckService.ExpandToSameKeyPendingAsync).</summary>
public class ProgressPaymentCheckServiceMatchTests
{
    private static (AppDbContext db, ProgressPaymentCheckService svc, ProgressPaymentCheck check, UnitPriceItem catalogItem) Seed()
    {
        var db = TestDbFactory.Create();
        var list = new UnitPriceList { CompanyName = "İNTİKOŞ", Region = "İÇ ANADOLU", Name = "Test Liste", IsActive = true, CreatedAt = DateTime.Now };
        db.UnitPriceLists.Add(list);
        db.SaveChanges();

        var catalogItem = new UnitPriceItem
        {
            UnitPriceListId = list.Id, MaterialName = "SOĞUTUCU GAZ 10,9 KG TÜP  (kg fiyatı)", Spec = "R404",
            Unit = "kg", Price = 13m, Currency = "EUR", NormalizedName = "sogutucu gaz 109 kg tup kg fiyati r404",
            IsActive = true, CreatedAt = DateTime.Now,
        };
        db.UnitPriceItems.Add(catalogItem);

        var check = new ProgressPaymentCheck
        {
            UnitPriceListId = list.Id, CompanyName = "İNTİKOŞ", Region = "İÇ ANADOLU",
            ClaimTypeName = "GAZ KULLANIM", Category = HakedisCategory.GasUsage, Year = 2026, Month = 5, PeriodLabel = "Mayıs 2026",
            OriginalFileName = "test.xlsx", OriginalFilePath = "test.xlsx",
            Status = ProgressPaymentCheckStatus.Taslak, CreatedAt = DateTime.Now,
        };
        db.ProgressPaymentChecks.Add(check);
        db.SaveChanges();

        var matching = new MaterialMatchingService(db);
        var unitPriceList = new UnitPriceListService(db, matching);
        var appPath = new FakeAppPathService();
        var svc = new ProgressPaymentCheckService(db, matching, appPath, unitPriceList);
        return (db, svc, check, catalogItem);
    }

    private static ProgressPaymentCheckItem PendingItem(int checkId, string storeName) => new()
    {
        ProgressPaymentCheckId = checkId, StoreName = storeName, StoreCode = storeName,
        OriginalMaterialName = "SOĞUTUCU GAZ 10,9 KG TÜP  (kg fiyatı)", OriginalMaterialSpec = null,
        IsServiceItem = false, Quantity = 20, Unit = "kg",
        CompanyUnitPrice = 664.43m, CompanyLineTotal = 664.43m * 20,
        MatchStatus = MaterialMatchStatus.FuzzyPending, MatchConfidence = 0.8684m,
        CreatedAt = DateTime.Now,
    };

    [Fact]
    public async Task ResolveMatchAsync_AyniHamAdaSahipDigerBekleyenKalemler_OtomatikEslesir()
    {
        var (db, svc, check, catalogItem) = Seed();

        var a = PendingItem(check.Id, "5M ANKARA");
        var b = PendingItem(check.Id, "KÜÇÜKESAT ANKARA M MİGROS");
        var c = PendingItem(check.Id, "GÖLBAŞI DM");
        // Farklı bir malzeme — bu satır ETKİLENMEMELİ.
        var other = PendingItem(check.Id, "AKSARAY MM MİGROS");
        other.OriginalMaterialName = "FARKLI MALZEME";
        db.ProgressPaymentCheckItems.AddRange(a, b, c, other);
        db.SaveChanges();

        // Yalnızca "a" satırını çözüyoruz — b ve c aynı ham ad/spec'e sahip, aynı kontrol içinde.
        await svc.ResolveMatchAsync(check.Id, new List<int> { a.Id }, catalogItem.Id, saveAsAlias: false, "İNTİKOŞ");

        var refreshed = db.ProgressPaymentCheckItems.ToList();
        Assert.Equal(MaterialMatchStatus.ManuallyMatched, refreshed.Single(i => i.Id == a.Id).MatchStatus);
        Assert.Equal(MaterialMatchStatus.ManuallyMatched, refreshed.Single(i => i.Id == b.Id).MatchStatus);
        Assert.Equal(MaterialMatchStatus.ManuallyMatched, refreshed.Single(i => i.Id == c.Id).MatchStatus);
        Assert.Equal(catalogItem.Id, refreshed.Single(i => i.Id == b.Id).MatchedUnitPriceItemId);
        Assert.Equal(catalogItem.Id, refreshed.Single(i => i.Id == c.Id).MatchedUnitPriceItemId);

        // Farklı malzeme adına sahip satır dokunulmamış (hâlâ FuzzyPending) kalmalı.
        Assert.Equal(MaterialMatchStatus.FuzzyPending, refreshed.Single(i => i.Id == other.Id).MatchStatus);
    }

    /// <summary>Kullanıcı talebi: "birini düzeltirsem aynı olan tüm malzemeler düzeltilmelidir" — yalnızca
    /// hâlâ FuzzyPending olanlar değil, DAHA ÖNCE (yanlış) bir kaleme otomatik/manuel eşleşmiş olan aynı
    /// ham ad/spec'e sahip kalemler de "Yeniden Eşleştir" ile düzeltildiğinde birlikte düzeltilmeli.</summary>
    [Fact]
    public async Task ResolveMatchAsync_DahaOnceYanlisEslesmisAyniHamAdliKalem_DeDuzeltilir()
    {
        var (db, svc, check, catalogItem) = Seed();

        var wrongCatalogItem = new UnitPriceItem
        {
            UnitPriceListId = catalogItem.UnitPriceListId, MaterialName = "YANLIŞ MALZEME", Spec = "X",
            Unit = "kg", Price = 1m, Currency = "EUR", NormalizedName = "yanlis malzeme x",
            IsActive = true, CreatedAt = DateTime.Now,
        };
        db.UnitPriceItems.Add(wrongCatalogItem);
        db.SaveChanges();

        var a = PendingItem(check.Id, "5M ANKARA");
        var b = PendingItem(check.Id, "KÜÇÜKESAT ANKARA M MİGROS");
        // b, daha önce YANLIŞ bir kaleme çoktan eşleştirilmiş (artık FuzzyPending değil) — gerçek olayda
        // aynı malzemenin farklı satırları farklı zamanlarda/nedenlerle yanlış eşleşmiş olabilir.
        b.MatchStatus = MaterialMatchStatus.ManuallyMatched;
        b.MatchedUnitPriceItemId = wrongCatalogItem.Id;
        b.MatchConfidence = 1.0m;
        db.ProgressPaymentCheckItems.AddRange(a, b);
        db.SaveChanges();

        await svc.ResolveMatchAsync(check.Id, new List<int> { a.Id }, catalogItem.Id, saveAsAlias: false, "İNTİKOŞ");

        var refreshedB = db.ProgressPaymentCheckItems.Single(i => i.Id == b.Id);
        Assert.Equal(catalogItem.Id, refreshedB.MatchedUnitPriceItemId);
        Assert.Equal(MaterialMatchStatus.ManuallyMatched, refreshedB.MatchStatus);
    }
}
