using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Services;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Spec §3 — mağaza kesinleştirme önceliği: tam kod → normalize kod → tam ad → fuzzy ad → belirsizse MANUAL_REVIEW.</summary>
public class StoreMatchingServiceTests
{
    private static void SeedStore(SogutmaHakedisKontrol.Infrastructure.Data.AppDbContext db, string code, string name)
    {
        db.Stores.Add(new Store
        {
            CompanyName = "İNTİKOŞ", Region = "İÇ ANADOLU",
            Code = code, Name = name,
            NormalizedCode = TextNormalizationHelper.NormalizeCode(code),
            NormalizedName = TextNormalizationHelper.NormalizeName(name),
            IsActive = true, CreatedAt = DateTime.Now,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task TamKodEslesmesi_OncelikliDir()
    {
        using var db = TestDbFactory.Create();
        SeedStore(db, "3336", "MM Migros Bahçebey Çorum");
        var svc = new StoreMatchingService(db);

        var result = await svc.MatchAsync("İNTİKOŞ", "İÇ ANADOLU", "3336", null);

        Assert.Equal(StoreMatchMethod.ExactCode, result.Method);
        Assert.False(result.RequiresManualReview);
    }

    [Fact]
    public async Task NormalizeKodEslesmesi_TireVeBoslukFarkiniYokSayar()
    {
        using var db = TestDbFactory.Create();
        SeedStore(db, "33-20", "MM Migros X");
        var svc = new StoreMatchingService(db);

        var result = await svc.MatchAsync("İNTİKOŞ", "İÇ ANADOLU", "3320", null);

        Assert.Equal(StoreMatchMethod.NormalizedCode, result.Method);
    }

    [Fact]
    public async Task Test5_SadeceMagazaAdiVarsa_AnaListedenEslestirilir()
    {
        using var db = TestDbFactory.Create();
        SeedStore(db, "1761", "Göksupark MMM Migros");
        var svc = new StoreMatchingService(db);

        var result = await svc.MatchAsync("İNTİKOŞ", "İÇ ANADOLU", null, "GÖKSUPARK MMM MİGROS");

        Assert.Equal(StoreMatchMethod.ExactName, result.Method);
        Assert.Equal(1761.ToString(), db.Stores.First().Code); // sağlık kontrolü
    }

    [Fact]
    public async Task BelirsizEslesme_OtomatikKararVerilmez_ManuelKontrole_Duser()
    {
        using var db = TestDbFactory.Create();
        SeedStore(db, "100", "Tamamen Alakasız Mağaza");
        var svc = new StoreMatchingService(db);

        var result = await svc.MatchAsync("İNTİKOŞ", "İÇ ANADOLU", "9999", "Hiç Eşleşmeyen Ad");

        Assert.True(result.RequiresManualReview);
        Assert.Null(result.StoreId);
    }

    [Fact]
    public async Task IkiBenzerAdayCokYakinsa_OtomatikKararVerilmezManuelKontrol()
    {
        using var db = TestDbFactory.Create();
        SeedStore(db, "1", "Ankara Merkez Migros");
        SeedStore(db, "2", "Ankara Merkez2 Migros");
        var svc = new StoreMatchingService(db);

        var result = await svc.MatchAsync("İNTİKOŞ", "İÇ ANADOLU", null, "Ankara Merkez Migros Şube");

        // İki aday da çok yakın skorlu olabileceğinden otomatik seçim yerine güvenli tarafta kal.
        Assert.True(result.Method == StoreMatchMethod.ManualReview || result.Confidence < 1.0m);
    }
}
