using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;
using MainClaim = HakedisOtomasyon.Domain.Entities.ProgressClaim;
using MainForm = HakedisOtomasyon.Domain.Entities.ServiceForm;
using MainItem = HakedisOtomasyon.Domain.Entities.ServiceItem;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma hakedişi: EUR fiyatlı kalemlerin hakediş kuruyla TL'ye çevrilmesi ve kur değişince
/// yeniden fiyatlanması. Yalnızca EUR kalemler etkilenir, TL ve diğer kalemler değişmez.</summary>
public class EurPricingTests
{
    private static MainDb CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eur_test_{Guid.NewGuid():N}.db");
        var db = new MainDb(new DbContextOptionsBuilder<MainDb>().UseSqlite($"Data Source={path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static ProgressClaimService CreateService(MainDb db) =>
        new(db, new CurrentDisciplineService { CurrentDiscipline = MechanicalDiscipline.Cooling });

    [Theory]
    [InlineData(9.945, 51.68, 513.96)]     // 513,9576 → 513,96
    [InlineData(2.805, 51.68, 144.96)]     // 144,9624 → 144,96
    [InlineData(1, 0.005, 0.01)]           // 0,005 yukarı yuvarlanır
    [InlineData(0, 51.68, 0)]
    public void ToTl_KuruskaYuvarlar(double eur, double rate, double expected)
        => Assert.Equal((decimal)expected, EurPricing.ToTl((decimal)eur, (decimal)rate));

    [Theory]
    [InlineData(null, false)]
    [InlineData(0.0, false)]
    [InlineData(-5.0, false)]
    [InlineData(51.68, true)]
    public void IsValidRate_SifirVeNegatifiReddeder(double? rate, bool valid)
        => Assert.Equal(valid, EurPricing.IsValidRate(rate.HasValue ? (decimal)rate.Value : null));

    [Fact]
    public void Reprice_SadeceEurKalemleriniGunceller()
    {
        var items = new List<ServiceItemDto>
        {
            new() { Description = "Fan", CurrencyCode = "EUR", ForeignPrice = 10m, Quantity = 3m, MaterialPrice = 400m },
            new() { Description = "Boru (TL)", MaterialPrice = 25m, Quantity = 2m },
            new() { Description = "USD kalem", CurrencyCode = "USD", ForeignPrice = 7m, MaterialPrice = 300m, Quantity = 1m },
        };
        var n = EurPricing.Reprice(items, 50m, new DateTime(2026, 10, 13), "Manuel");

        Assert.Equal(1, n);
        Assert.Equal(500m, items[0].MaterialPrice);
        Assert.Equal(500m, items[0].UnitPrice);
        Assert.Equal(1500m, items[0].TotalPrice);
        Assert.Equal(50m, items[0].ExchangeRate);
        Assert.Equal(25m, items[1].MaterialPrice);   // TL kalem değişmedi
        Assert.Equal(300m, items[2].MaterialPrice);  // USD kalem değişmedi
    }

    private static async Task<(MainClaim claim, MainForm form1, MainForm form2)> SeedClaimAsync(MainDb db)
    {
        var claim = new MainClaim { Name = "Test", CompanyName = "X", Month = 10, Year = 2026, Discipline = MechanicalDiscipline.Cooling };
        db.ProgressClaims.Add(claim);
        await db.SaveChangesAsync();
        var f1 = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        var f2 = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "b.pdf" };
        db.ServiceForms.AddRange(f1, f2);
        await db.SaveChangesAsync();

        db.ServiceItems.AddRange(
            new MainItem { ServiceFormId = f1.Id, Description = "EUR kalem", Quantity = 2m, CurrencyCode = "EUR", ForeignPrice = 10m, MaterialPrice = 400m, UnitPrice = 400m, TotalPrice = 800m },
            new MainItem { ServiceFormId = f1.Id, Description = "TL kalem", Quantity = 1m, MaterialPrice = 75m, UnitPrice = 75m, TotalPrice = 75m },
            new MainItem { ServiceFormId = f2.Id, Description = "EUR kalem 2", Quantity = 1m, CurrencyCode = "EUR", ForeignPrice = 4.5m, MaterialPrice = 180m, UnitPrice = 180m, TotalPrice = 180m });
        await db.SaveChangesAsync();
        return (claim, f1, f2);
    }

    [Fact]
    public async Task UpdateEurRate_HakedistekiTumFormlarinEurKalemleriniYenidenFiyatlar()
    {
        using var db = CreateDb();
        var (claim, _, _) = await SeedClaimAsync(db);
        var svc = CreateService(db);

        var n = await svc.UpdateEurRateAsync(claim.Id, 50m, new DateTime(2026, 10, 13), "TCMB");

        Assert.Equal(2, n);
        var eur1 = await db.ServiceItems.SingleAsync(i => i.Description == "EUR kalem");
        Assert.Equal(500m, eur1.MaterialPrice);
        Assert.Equal(1000m, eur1.TotalPrice);          // 2 adet × 500
        Assert.Equal(50m, eur1.ExchangeRate);
        Assert.Equal("TCMB", eur1.ExchangeRateSource);
        var eur2 = await db.ServiceItems.SingleAsync(i => i.Description == "EUR kalem 2");
        Assert.Equal(225m, eur2.MaterialPrice);        // 4,5 × 50, diğer formdaki kalem de güncellendi
        var tl = await db.ServiceItems.SingleAsync(i => i.Description == "TL kalem");
        Assert.Equal(75m, tl.MaterialPrice);           // TL kalem dokunulmadı
        var saved = await db.ProgressClaims.SingleAsync();
        Assert.Equal(50m, saved.EurRate);
        Assert.Equal("TCMB", saved.EurRateSource);
    }

    [Fact]
    public async Task UpdateEurRate_KurSifirsaReddederVeFiyatlariBozmaz()
    {
        using var db = CreateDb();
        var (claim, _, _) = await SeedClaimAsync(db);
        var svc = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateEurRateAsync(claim.Id, 0m, null, "Manuel"));

        Assert.Equal(400m, (await db.ServiceItems.SingleAsync(i => i.Description == "EUR kalem")).MaterialPrice);
        Assert.Null((await db.ProgressClaims.SingleAsync()).EurRate);
    }

    [Fact]
    public async Task UpdateAsync_HakedisBilgilerindeKurDegisirseKalemlerYenidenFiyatlanir()
    {
        using var db = CreateDb();
        var (claim, _, _) = await SeedClaimAsync(db);
        var svc = CreateService(db);

        var dto = (await svc.GetByIdAsync(claim.Id))!;
        dto.EurRate = 60m;
        dto.EurRateDate = new DateTime(2026, 10, 13);
        dto.EurRateSource = "Manuel";
        await svc.UpdateAsync(dto);

        Assert.Equal(600m, (await db.ServiceItems.SingleAsync(i => i.Description == "EUR kalem")).MaterialPrice);
        Assert.Equal(60m, (await svc.GetByIdAsync(claim.Id))!.EurRate);
    }

    [Fact]
    public async Task CreateAsync_EurKuruKaydedilirVeGeriOkunur()
    {
        using var db = CreateDb();
        var svc = CreateService(db);

        var created = await svc.CreateAsync(new ProgressClaimDto
        {
            Name = "Soğutma Ekim", CompanyName = "İNTİKOŞ", Month = 10, Year = 2026,
            EurRate = 51.68m, EurRateDate = new DateTime(2026, 10, 13), EurRateSource = "TCMB",
        });

        var read = await svc.GetByIdAsync(created.Id);
        Assert.Equal(51.68m, read!.EurRate);
        Assert.Equal("TCMB", read.EurRateSource);
        Assert.Equal(MechanicalDiscipline.Cooling, read.Discipline);
    }

    [Fact]
    public async Task ServiceItemDto_ForeignPriceKaydedilipGeriOkunur()
    {
        using var db = CreateDb();
        var (_, f1, _) = await SeedClaimAsync(db);
        var formService = new ServiceFormService(db, new FakeFileStorage(), null!, null!);

        await formService.SaveItemsAsync(f1.Id, new List<ServiceItemDto>
        {
            new() { Description = "EUR kalem", Quantity = 1m, CurrencyCode = "EUR", ForeignPrice = 9.945m, MaterialPrice = 513.96m,
                    UnitPrice = 513.96m, TotalPrice = 513.96m, ExchangeRate = 51.68m, ExchangeRateSource = "Manuel" },
        });

        var forms = await formService.GetByClaimIdAsync(f1.ProgressClaimId);
        var item = forms.Single(f => f.Id == f1.Id).ServiceItems.Single();
        Assert.Equal(9.945m, item.ForeignPrice);
        Assert.Equal("EUR", item.CurrencyCode);
        Assert.Equal(51.68m, item.ExchangeRate);
    }
}

/// <summary>Yalnızca yol/URL dönüşümü gereken testler için sahte dosya depolama.</summary>
internal sealed class FakeFileStorage : HakedisOtomasyon.Application.Interfaces.IFileStorageService
{
    public Task<string> SaveServiceFormAsync(Stream stream, string fileName, int year, int month) => throw new NotSupportedException();
    public Task<string> SaveInvoiceAsync(Stream stream, string fileName, int year, int month) => throw new NotSupportedException();
    public Task<string> SaveExportAsync(Stream stream, string fileName, int year, int month) => throw new NotSupportedException();
    public Task<string> CopyServiceFormAsync(string sourcePath, int year, int month) => throw new NotSupportedException();
    public Task<string> CopyInvoiceAsync(string sourcePath, int year, int month) => throw new NotSupportedException();
    public string GetAbsolutePath(string storedPath) => storedPath;
    public string GetFileUrl(string storedPath) => storedPath;
    public bool FileExists(string storedPath) => false;
    public void DeleteFile(string storedPath) { }
}
