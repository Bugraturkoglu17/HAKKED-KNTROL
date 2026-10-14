using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Domain.Enums;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma hakedişi oluşturulurken seçilen hakediş türü (Glikol Kullanım vb.).</summary>
public class CoolingClaimCategoryTests
{
    private static MainDb CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat_test_{Guid.NewGuid():N}.db");
        var db = new MainDb(new DbContextOptionsBuilder<MainDb>().UseSqlite($"Data Source={path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static ProgressClaimService Service(MainDb db, MechanicalDiscipline discipline) =>
        new(db, new CurrentDisciplineService { CurrentDiscipline = discipline });

    [Fact]
    public void KompresorGlikolKismiTadilatGazVeBakimOnarimSecilebilir_DigerleriYakinda()
    {
        var available = Enum.GetValues<CoolingClaimCategory>().Where(c => c.IsAvailable()).ToList();
        Assert.Equal(new[] { CoolingClaimCategory.CompressorReplacement, CoolingClaimCategory.GlycolUsage, CoolingClaimCategory.EvapReplacement, CoolingClaimCategory.PartialRenovation, CoolingClaimCategory.GasUsage, CoolingClaimCategory.MaintenanceRepair }, available);
        Assert.Equal(8, Enum.GetValues<CoolingClaimCategory>().Length);
    }

    /// <summary>Hakediş oluşturma ile Hakediş Kontrolü aynı 8 numarayı kullanır (ileride iki taraf birbirine bağlanacak).
    /// Tek fark 7 numaradır: oluşturma tarafında "İlave İşler" başlığı kaldırılıp yerine "Bakım &amp; Onarım" konmuştur;
    /// Hakediş Kontrolü tarafındaki "İlave İşler" kontrolü yerinde durur.</summary>
    [Fact]
    public void Turler_HakedisKontroluKategorileriyleAyniNumarada_YalnizIlaveIslerYerineBakimOnarim()
    {
        var creation = Enum.GetValues<CoolingClaimCategory>();
        var control = Enum.GetValues<HakedisCategory>();
        Assert.Equal(control.Length, creation.Length);
        foreach (var c in creation)
        {
            var counterpart = (HakedisCategory)(int)c;
            if (c == CoolingClaimCategory.MaintenanceRepair)
            {
                Assert.Equal(HakedisCategory.AdditionalWork, counterpart);
                Assert.Equal("Bakım & Onarım", c.DisplayName());
                continue;
            }
            Assert.Equal(counterpart.ToString(), c.ToString());
            Assert.Equal(counterpart.DisplayName(), c.DisplayName());
        }
        Assert.DoesNotContain(creation, c => c.DisplayName() == "İlave İşler");
    }

    [Fact]
    public async Task SogutmaHakedisiOlusturulurken_TurKaydedilirVeGeriOkunur()
    {
        using var db = CreateDb();
        var svc = Service(db, MechanicalDiscipline.Cooling);

        var created = await svc.CreateAsync(new ProgressClaimDto
        {
            Name = "Eylül Glikol", CompanyName = "İNTİKOŞ", Month = 9, Year = 2026,
            CoolingCategory = CoolingClaimCategory.GlycolUsage,
        });

        var read = await svc.GetByIdAsync(created.Id);
        Assert.Equal(CoolingClaimCategory.GlycolUsage, read!.CoolingCategory);
        Assert.Equal("Glikol Kullanım", read.CoolingCategoryLabel);
        Assert.Equal(CoolingClaimCategory.GlycolUsage, (await svc.GetAllAsync()).Single().CoolingCategory);
    }

    [Fact]
    public async Task TurHakedisBilgilerindenDegistirilebilir()
    {
        using var db = CreateDb();
        var svc = Service(db, MechanicalDiscipline.Cooling);
        var created = await svc.CreateAsync(new ProgressClaimDto { Name = "A", CompanyName = "X", CoolingCategory = CoolingClaimCategory.GasUsage });

        var dto = (await svc.GetByIdAsync(created.Id))!;
        dto.CoolingCategory = CoolingClaimCategory.GlycolUsage;
        await svc.UpdateAsync(dto);

        Assert.Equal(CoolingClaimCategory.GlycolUsage, (await svc.GetByIdAsync(created.Id))!.CoolingCategory);
    }

    [Fact]
    public async Task YanginHakedisindeSogutmaTuruSaklanmaz()
    {
        using var db = CreateDb();
        var svc = Service(db, MechanicalDiscipline.Fire);

        var created = await svc.CreateAsync(new ProgressClaimDto
        {
            Name = "Yangın Ekim", CompanyName = "SİSMİK", CoolingCategory = CoolingClaimCategory.GlycolUsage,
        });

        Assert.Null((await svc.GetByIdAsync(created.Id))!.CoolingCategory);
        Assert.Equal(string.Empty, (await svc.GetByIdAsync(created.Id))!.CoolingCategoryLabel);
    }

    [Fact]
    public async Task EskiSogutmaHakedisi_TurYoksaBosKalirHataVermez()
    {
        using var db = CreateDb();
        var svc = Service(db, MechanicalDiscipline.Cooling);
        var created = await svc.CreateAsync(new ProgressClaimDto { Name = "Eski", CompanyName = "X" });

        var read = await svc.GetByIdAsync(created.Id);

        Assert.Null(read!.CoolingCategory);
        Assert.Equal(string.Empty, read.CoolingCategoryLabel);
    }

    /// <summary>Hakediş oluşturulunca sayfa elindeki DTO'yu kullanır; disiplin/tür bilgisi DTO'da da dolu olmalı
    /// (aksi halde yeni Soğutma hakedişi sayfada "Yangın" sanılır ve kur/tür alanları kaybolurdu).</summary>
    [Fact]
    public async Task CreateAsync_DonenDtoDisiplinVeTuruIcerir()
    {
        using var db = CreateDb();
        var svc = Service(db, MechanicalDiscipline.Cooling);

        var returned = await svc.CreateAsync(new ProgressClaimDto
        {
            Name = "A", CompanyName = "X", CoolingCategory = CoolingClaimCategory.GlycolUsage,
        });

        Assert.Equal(MechanicalDiscipline.Cooling, returned.Discipline);
        Assert.Equal(CoolingClaimCategory.GlycolUsage, returned.CoolingCategory);
        Assert.True(HakedisOtomasyon.Application.GlycolClaim.IsGlycol(returned.Discipline, returned.CoolingCategory));
    }
}
