using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;
using MainPriceItem = HakedisOtomasyon.Domain.Entities.PriceItem;
using MainStore = HakedisOtomasyon.Domain.Entities.Store;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma başlangıç verisi (kurulumla gelen fiyat listesi + mağazalar): doğru para birimi,
/// yalnızca Soğutma disiplinine yazma ve kullanıcının mevcut verisini koruma.</summary>
public class CoolingSeedServiceTests
{
    private static MainDb CreateMainDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"main_seed_test_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<MainDb>().UseSqlite($"Data Source={path}").Options;
        var db = new MainDb(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>Repodaki gerçek seed dosyalarının klasörü (testin çalıştığı yerden yukarı doğru aranır).</summary>
    private static string SeedDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "HakedisOtomasyon.Web", "SeedData");
            if (File.Exists(Path.Combine(candidate, CoolingSeedService.PriceFileName))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("SeedData klasörü bulunamadı.");
    }

    [Fact]
    public async Task BosVeritabaninda_FiyatlarVeMagazalarSadeceSogutmaOlarakEklenir()
    {
        using var db = CreateMainDb();
        var result = await CoolingSeedService.SeedIfNeededAsync(db, SeedDir());

        Assert.True(result.Applied);
        Assert.Equal(253, result.PriceItemsAdded);
        Assert.Equal(4141, result.StoresAdded);
        Assert.Equal(253, await db.PriceItems.CountAsync(p => p.Discipline == MechanicalDiscipline.Cooling));
        Assert.Equal(4141, await db.Stores.CountAsync(s => s.Discipline == MechanicalDiscipline.Cooling));
        // Başka hiçbir disipline kayıt yazılmamalı
        Assert.Equal(0, await db.PriceItems.CountAsync(p => p.Discipline != MechanicalDiscipline.Cooling));
        Assert.Equal(0, await db.Stores.CountAsync(s => s.Discipline != MechanicalDiscipline.Cooling));
    }

    [Fact]
    public async Task ParaBirimiKorunur_EurKalemlerTlAlanlarinaYazilmaz()
    {
        using var db = CreateMainDb();
        await CoolingSeedService.SeedIfNeededAsync(db, SeedDir());

        var eur = await db.PriceItems.Where(p => p.CurrencyCode == "EUR").ToListAsync();
        var tl = await db.PriceItems.Where(p => p.CurrencyCode == null).ToListAsync();
        Assert.Equal(215, eur.Count);
        Assert.Equal(38, tl.Count);
        // EUR: tutar ForeignPrice'ta, TL alanları 0 — EUR tutar asla TL gibi saklanmaz
        Assert.All(eur, p =>
        {
            Assert.True(p.IsCurrencyBased);
            Assert.True(p.ForeignPrice.HasValue);
            Assert.Equal(0m, p.MaterialPrice);
            Assert.Equal(0m, p.LaborPrice);
        });
        // TL: tutar MaterialPrice'ta, döviz alanı boş
        Assert.All(tl, p => { Assert.False(p.IsCurrencyBased); Assert.Null(p.ForeignPrice); });
        // Bilinen örnekler (kaynak xls): "DOLAP FAN MOTORU 50 W" 9,945 EUR; "TADİLAT ERİŞİM ŞEHİRİÇİ" 2750 TL
        Assert.Contains(eur, p => p.Description.StartsWith("DOLAP FAN MOTORU") && p.ForeignPrice == 9.945m);
        Assert.Contains(tl, p => p.Description.StartsWith("TADİLAT ERİŞİM") && p.Description.Contains("ŞEHİRİÇİ") && p.MaterialPrice == 2750m);
    }

    [Fact]
    public async Task IkinciCalistirmaHicBirSeyEklemez()
    {
        using var db = CreateMainDb();
        await CoolingSeedService.SeedIfNeededAsync(db, SeedDir());
        var second = await CoolingSeedService.SeedIfNeededAsync(db, SeedDir());

        Assert.False(second.Applied);
        Assert.Equal(253, await db.PriceItems.CountAsync());
        Assert.Equal(4141, await db.Stores.CountAsync());
    }

    [Fact]
    public async Task YanginVerisiKorunur_AyniMagazaKoduIkiDisiplindeYanYanaDurur()
    {
        using var db = CreateMainDb();
        db.Stores.Add(new MainStore { Code = "1000", Name = "YANGIN MAĞAZA 1000", Discipline = MechanicalDiscipline.Fire });
        db.PriceItems.Add(new MainPriceItem { Description = "Yangın Borusu DN100", Unit = "m", MaterialPrice = 500m, Discipline = MechanicalDiscipline.Fire });
        await db.SaveChangesAsync();

        await CoolingSeedService.SeedIfNeededAsync(db, SeedDir());

        var fireStore = await db.Stores.SingleAsync(s => s.Code == "1000" && s.Discipline == MechanicalDiscipline.Fire);
        Assert.Equal("YANGIN MAĞAZA 1000", fireStore.Name); // Soğutma'daki "1000" Yangın'ı ezmedi
        Assert.Equal(1, await db.Stores.CountAsync(s => s.Code == "1000" && s.Discipline == MechanicalDiscipline.Cooling));
        var firePrice = await db.PriceItems.SingleAsync(p => p.Discipline == MechanicalDiscipline.Fire);
        Assert.Equal(500m, firePrice.MaterialPrice);
        Assert.Equal(253, await db.PriceItems.CountAsync(p => p.Discipline == MechanicalDiscipline.Cooling));
    }

    [Fact]
    public async Task SogutmadaZatenKalemVarsaFiyatlarEklenmez_AmaMagazalarEklenir()
    {
        using var db = CreateMainDb();
        db.PriceItems.Add(new MainPriceItem { Description = "Kullanıcının kendi kalemi", Unit = "ad", MaterialPrice = 10m, Discipline = MechanicalDiscipline.Cooling });
        await db.SaveChangesAsync();

        var result = await CoolingSeedService.SeedIfNeededAsync(db, SeedDir());

        Assert.True(result.Applied);
        Assert.Equal(0, result.PriceItemsAdded);
        Assert.Equal(1, await db.PriceItems.CountAsync(p => p.Discipline == MechanicalDiscipline.Cooling));
        Assert.Equal(4141, result.StoresAdded);
    }

    [Fact]
    public async Task SeedDosyalariYoksaBayrakKonmaz()
    {
        using var db = CreateMainDb();
        var empty = Path.Combine(Path.GetTempPath(), $"bos_{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);

        var result = await CoolingSeedService.SeedIfNeededAsync(db, empty);

        Assert.False(result.Applied);
        Assert.False(await db.AppSettings.AnyAsync(s => s.Key == CoolingSeedService.SeedFlagKey));
    }

    /// <summary>Yedekten/eski veritabanından geri yükleme disiplinleri karıştırmamalı: Soğutma kayıtları
    /// Yangın'a düşmemeli, aynı mağaza kodu iki disiplinde ayrı kalmalı, EUR fiyat korunmalı.</summary>
    [Fact]
    public async Task EskiVeritabanindanGeriYuklemeDisiplinleriKaristirmaz()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"main_restore_src_{Guid.NewGuid():N}.db");
        using (var src = new MainDb(new DbContextOptionsBuilder<MainDb>().UseSqlite($"Data Source={sourcePath}").Options))
        {
            src.Database.EnsureCreated();
            src.Stores.Add(new MainStore { Code = "1000", Name = "YANGIN 1000", Discipline = MechanicalDiscipline.Fire });
            src.Stores.Add(new MainStore { Code = "1000", Name = "SOĞUTMA 1000", Discipline = MechanicalDiscipline.Cooling });
            src.PriceItems.Add(new MainPriceItem { Description = "Boru", Unit = "m", MaterialPrice = 100m, Discipline = MechanicalDiscipline.Fire });
            src.PriceItems.Add(new MainPriceItem
            {
                Description = "Boru", Unit = "m", Discipline = MechanicalDiscipline.Cooling,
                IsCurrencyBased = true, CurrencyCode = "EUR", ForeignPrice = 4.5m, ExchangeRateRequired = true,
            });
            await src.SaveChangesAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var target = CreateMainDb();
        var recovery = new DataRecoveryService(target, null!);
        var result = await recovery.RestoreFromDatabaseAsync(sourcePath, true, true, false);

        Assert.Empty(result.Errors);
        Assert.Equal("YANGIN 1000", (await target.Stores.SingleAsync(s => s.Discipline == MechanicalDiscipline.Fire)).Name);
        Assert.Equal("SOĞUTMA 1000", (await target.Stores.SingleAsync(s => s.Discipline == MechanicalDiscipline.Cooling)).Name);
        var fire = await target.PriceItems.SingleAsync(p => p.Discipline == MechanicalDiscipline.Fire);
        Assert.Equal(100m, fire.MaterialPrice);
        Assert.Null(fire.ForeignPrice);
        var cooling = await target.PriceItems.SingleAsync(p => p.Discipline == MechanicalDiscipline.Cooling);
        Assert.Equal(4.5m, cooling.ForeignPrice);
        Assert.Equal("EUR", cooling.CurrencyCode);
        Assert.Equal(0m, cooling.MaterialPrice);
    }
}
