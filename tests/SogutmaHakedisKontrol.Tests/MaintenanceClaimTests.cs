using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.ExcelExport;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MainDb = HakedisOtomasyon.Infrastructure.Data.AppDbContext;
using MainClaim = HakedisOtomasyon.Domain.Entities.ProgressClaim;
using MainForm = HakedisOtomasyon.Domain.Entities.ServiceForm;
using MainItem = HakedisOtomasyon.Domain.Entities.ServiceItem;
using MainStore = HakedisOtomasyon.Domain.Entities.Store;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma "Bakım &amp; Onarım" hakedişi: her forma mağazanın iline göre otomatik şehir içi/dışı servis bedeli;
/// Excel çıktısı onaylı örnek tabloyla (386 BAHÇELİEVLER bloğu → GENEL TOPLAM 33.496,69 TL) aynı olmalı.</summary>
public partial class MaintenanceClaimTests
{
    private const decimal Rate = 53.29m;   // örnek tablodaki EUR kuru (59,5595 EUR → 3.173,93 TL)

    // ---------------------------------------------------------------- şehir içi / şehir dışı
    [Theory]
    [InlineData("ANKARA", true)]
    [InlineData("Ankara", true)]
    [InlineData("  ankara ", true)]
    [InlineData("ESKİŞEHİR", false)]
    [InlineData("ZONGULDAK", false)]
    [InlineData("KONYA", false)]
    public void AnkaraSehirIci_DigerIllerSehirDisi(string city, bool inCity)
        => Assert.Equal(inCity, MaintenanceClaim.IsInCity(city));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IlKayitliDegilse_KararVerilmez(string? city) => Assert.Null(MaintenanceClaim.IsInCity(city));

    private static PriceItemDto InCityFee => new()
    { PozNo = "S1", Description = "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ 1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", Unit = "set", MaterialPrice = 2750m };
    private static PriceItemDto OutCityFee => new()
    { PozNo = "S2", Description = "1 EKIP ŞEHİR DIŞI SERVİS BEDELİ 1 EKIP ŞEHİR DIŞI SERVİS BEDELİ", Unit = "set", MaterialPrice = 4200m };

    [Fact]
    public void AnkaraMagazasi_SehirIciServisBedeliOtomatikEnBasaEklenir()
    {
        var items = new List<ServiceItemDto> { new() { Description = "EXPANSİON VALVE", Quantity = 2, MaterialPrice = 100m, SortOrder = 1 } };

        var result = MaintenanceClaim.ApplyServiceFee(items, "ANKARA", InCityFee, OutCityFee);

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, result);
        Assert.Equal(2, items.Count);
        var fee = items[0];                                              // en başta
        Assert.Equal("1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", fee.Description);  // iki kez yazılı ad teke indi
        Assert.Equal("S1", fee.ItemCode);
        Assert.Equal("set", fee.Unit);
        Assert.Equal(1m, fee.Quantity);
        Assert.Equal(2750m, fee.UnitPrice);
        Assert.Equal(2750m, fee.TotalPrice);
        Assert.Null(fee.ItemType);
        Assert.Equal(new[] { 1, 2 }, items.Select(i => i.SortOrder).ToArray());
    }

    [Fact]
    public void AnkaraDisiMagaza_SehirDisiServisBedeliEklenir()
    {
        var items = new List<ServiceItemDto>();

        var result = MaintenanceClaim.ApplyServiceFee(items, "ESKİŞEHİR", InCityFee, OutCityFee);

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, result);
        var fee = Assert.Single(items);
        Assert.Equal("1 EKIP ŞEHİR DIŞI SERVİS BEDELİ", fee.Description);
        Assert.Equal("S2", fee.ItemCode);
        Assert.Equal(4200m, fee.TotalPrice);
    }

    [Fact]
    public void AyniMagazaTekrarSecilince_ServisBedeliIkinciKezEklenmez()
    {
        var items = new List<ServiceItemDto>();
        MaintenanceClaim.ApplyServiceFee(items, "ANKARA", InCityFee, OutCityFee);
        items[0].Quantity = 2; items[0].Recalculate();     // kullanıcı miktarı değiştirdi

        var again = MaintenanceClaim.ApplyServiceFee(items, "Ankara", InCityFee, OutCityFee);

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Unchanged, again);
        Assert.Equal(2m, Assert.Single(items).Quantity);   // dokunulmadı
    }

    [Fact]
    public void MagazaDegisipIlTuruDegisince_BedelDegisirMiktarKorunur()
    {
        var items = new List<ServiceItemDto>();
        MaintenanceClaim.ApplyServiceFee(items, "ANKARA", InCityFee, OutCityFee);
        items.Add(new ServiceItemDto { Description = "ORFİS", Quantity = 1, MaterialPrice = 10m, SortOrder = 2 });
        items[0].Quantity = 2; items[0].Recalculate();

        var result = MaintenanceClaim.ApplyServiceFee(items, "KONYA", InCityFee, OutCityFee);

        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Swapped, result);
        Assert.Equal(2, items.Count);                                  // kopya oluşmadı
        Assert.Equal("S2", items[0].ItemCode);
        Assert.Equal("1 EKIP ŞEHİR DIŞI SERVİS BEDELİ", items[0].Description);
        Assert.Equal(2m, items[0].Quantity);
        Assert.Equal(8400m, items[0].TotalPrice);
    }

    [Fact]
    public void IlBilinmiyorsa_BedelEklenmezListeDegismez()
    {
        var items = new List<ServiceItemDto>();
        Assert.Equal(MaintenanceClaim.ServiceFeeResult.UnknownCity, MaintenanceClaim.ApplyServiceFee(items, null, InCityFee, OutCityFee));
        Assert.Empty(items);
    }

    [Fact]
    public void FiyatListesindeBedelKalemiYoksa_Bildirilir()
    {
        var items = new List<ServiceItemDto>();
        Assert.Equal(MaintenanceClaim.ServiceFeeResult.MissingPriceItem, MaintenanceClaim.ApplyServiceFee(items, "ANKARA", null, OutCityFee));
        Assert.Empty(items);
    }

    [Fact]
    public void FindFee_FiyatListesindenAdaVeyaKodaGoreBulur()
    {
        var list = new List<PriceItemDto>
        {
            new() { PozNo = "35", Description = "EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI" },
            new() { PozNo = "247", Description = "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ 1 EKIP ŞEHİR İÇİ SERVİS BEDELİ" },   // eski kodlu liste
            new() { PozNo = "S2", Description = "Şehir dışı ekip" },                                                    // adı değişmiş, kodu doğru
        };
        Assert.Equal("247", MaintenanceClaim.FindFee(list, inCity: true)!.PozNo);
        Assert.Equal("S2", MaintenanceClaim.FindFee(list, inCity: false)!.PozNo);
    }

    // ---------------------------------------------------------------- malzeme adı / tipi
    [Theory]
    [InlineData("EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", "DIŞ DENGELİ VE KAYNAKLI", "EXPANSİON VALVE", "DIŞ DENGELİ VE KAYNAKLI")]
    [InlineData("CAREL FAN HIZ KONTROL MONOFAZE FCSM122300 12 AMPER MONO", "FCSM122300 12 AMPER MONO", "CAREL FAN HIZ KONTROL MONOFAZE", "FCSM122300 12 AMPER MONO")]
    [InlineData("1 EKIP ŞEHİR İÇİ SERVİS BEDELİ 1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", null, "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", "")]
    [InlineData("ADAM SAAT GUNDUZ /GECE (>=2. GUN) ADAM SAAT GUNDUZ /GECE (>=2. GUN)", "", "ADAM SAAT GUNDUZ /GECE (>=2. GUN)", "")]
    [InlineData("ORFİS", null, "ORFİS", "")]
    [InlineData("Elle girilen kalem", "ÖZEL TİP", "Elle girilen kalem", "ÖZEL TİP")]
    public void SplitName_AdVeTipiAyirir_TipYoksaBosBirakir(string description, string? type, string expectedName, string expectedType)
    {
        var (name, t) = MaintenanceClaim.SplitName(description, type);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedType, t);
    }

    // ---------------------------------------------------------------- servis (veritabanı)
    private static MainDb CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maint_test_{Guid.NewGuid():N}.db");
        var db = new MainDb(new DbContextOptionsBuilder<MainDb>().UseSqlite($"Data Source={path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task<(ServiceFormService svc, MainClaim claim)> SeedAsync(MainDb db, CoolingClaimCategory category = CoolingClaimCategory.MaintenanceRepair)
    {
        var claim = new MainClaim
        {
            Name = "Temmuz Bakım", CompanyName = "İNTİKOŞ", Month = 7, Year = 2026, Discipline = MechanicalDiscipline.Cooling,
            CoolingCategory = category, EurRate = Rate,
        };
        db.ProgressClaims.Add(claim);
        await db.SaveChangesAsync();
        return (new ServiceFormService(db, new FakeFileStorage(), null!, null!), claim);
    }

    [Fact]
    public async Task FormEklenince_BakimOnarimHakedisindeSabitServisUcretiEklenmez()
    {
        using var db = CreateDb();
        var (svc, claim) = await SeedAsync(db);

        var form = await svc.AddFormAsync(claim.Id, "forms/x.pdf", "x.pdf");

        Assert.Empty(form.ServiceItems);   // bedel, mağaza seçilince ile göre eklenir
        Assert.Empty(await db.ServiceItems.Where(i => i.ServiceFormId == form.Id).ToListAsync());
    }

    [Fact]
    public async Task KalemKoduTipiVeMagazaIli_KaydedilipGeriOkunur()
    {
        using var db = CreateDb();
        var (svc, claim) = await SeedAsync(db);
        db.Stores.Add(new MainStore { Code = "386", Name = "BAHÇELİEVLER ANKARA MM MİGROS", Format = "MM", Region = "İÇ ANADOLU", City = "ANKARA", Discipline = MechanicalDiscipline.Cooling });
        await db.SaveChangesAsync();
        var form = await svc.AddFormAsync(claim.Id, "forms/x.pdf", "x.pdf");

        await svc.UpdateStoreAsync(form.Id, "386");
        await svc.SaveItemsAsync(form.Id, new List<ServiceItemDto>
        {
            new() { Description = "EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", ItemCode = "35", ItemType = "DIŞ DENGELİ VE KAYNAKLI", Unit = "ADET", Quantity = 2 },
        });

        var dto = (await svc.GetByIdAsync(form.Id))!;
        Assert.Equal("ANKARA", dto.StoreCity);
        var item = Assert.Single(dto.ServiceItems);
        Assert.Equal("35", item.ItemCode);
        Assert.Equal("DIŞ DENGELİ VE KAYNAKLI", item.ItemType);
    }

    // ---------------------------------------------------------------- Excel çıktısı
    private static MainStore Store(string code, string name, string format, string city = "ANKARA") =>
        new() { Code = code, Name = name, Format = format, Region = "İÇ ANADOLU", City = city, Discipline = MechanicalDiscipline.Cooling };

    private static MainItem Tl(string code, string desc, decimal qty, decimal price, string unit = "set", string? type = null) => new()
    { ItemCode = code, Description = desc, ItemType = type, Quantity = qty, Unit = unit, MaterialPrice = price, UnitPrice = price, TotalPrice = qty * price };

    private static MainItem Eur(string code, string desc, string? type, decimal qty, decimal eur) => new()
    {
        ItemCode = code, Description = desc, ItemType = type, Quantity = qty, Unit = "ADET", CurrencyCode = "EUR", ForeignPrice = eur, ExchangeRate = Rate,
        ExchangeRateSource = "Manuel", ExchangeRateDate = new DateTime(2026, 8, 1),
        MaterialPrice = EurPricing.ToTl(eur, Rate), UnitPrice = EurPricing.ToTl(eur, Rate), TotalPrice = EurPricing.TotalTl(qty, eur, Rate),
    };

    private static MainForm Form(int id, MainStore store, DateTime date, string no, params MainItem[] items)
    {
        for (int i = 0; i < items.Length; i++) items[i].SortOrder = i + 1;
        return new MainForm { Id = id, Store = store, StoreId = 1, FormDate = date, FormNumber = no, OriginalFileName = $"f{id}.pdf", Status = ServiceFormStatus.Tamamlandi, ServiceItems = items.ToList() };
    }

    private const string S1 = "1 EKIP ŞEHİR İÇİ SERVİS BEDELİ";
    private const string S3 = "ADAM SAAT GUNDUZ /GECE (>=2. GUN)";

    /// <summary>Örnek tablodaki ilk 11 satır: 386 BAHÇELİEVLER (4 form, 10 kalem) + 390 KÜÇÜKESAT (1 form, 1 kalem). BİLEREK karışık sırada.</summary>
    private static (MainClaim claim, List<MainForm> forms) ScreenshotData()
    {
        var bahceli = Store("386", "BAHÇELİEVLER ANKARA MM MİGROS", "MM");
        var kucukesat = Store("390", "KÜÇÜKESAT ANKARA M MİGROS", "SERVİS REYONLU M");
        var claim = new MainClaim
        {
            Id = 1, Name = "Temmuz Bakım", CompanyName = "İNTİKOŞ", Month = 7, Year = 2026,
            Discipline = MechanicalDiscipline.Cooling, CoolingCategory = CoolingClaimCategory.MaintenanceRepair, EurRate = Rate,
        };
        var forms = new List<MainForm>
        {
            Form(5, kucukesat, new DateTime(2026, 7, 14), "21140", Tl("S1", S1, 1, 2750m)),
            Form(3, bahceli, new DateTime(2026, 7, 25), "21183",
                Tl("S1", S1, 1, 2750m),
                Eur("35", "EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", "DIŞ DENGELİ VE KAYNAKLI", 1, 59.5595m)),
            Form(2, bahceli, new DateTime(2026, 7, 24), "21179",
                Tl("S1", S1, 1, 2750m),
                Tl("S3", S3, 8, 750m),
                Eur("35", "EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", "DIŞ DENGELİ VE KAYNAKLI", 2, 59.5595m),
                Eur("36", "ORFİS", null, 2, 9.656m),
                Eur("61", "CAREL FAN HIZ KONTROL MONOFAZE FCSM122300 12 AMPER MONO", "FCSM122300 12 AMPER MONO", 1, 97.5m)),
            Form(1, bahceli, new DateTime(2026, 7, 23), "21169", Tl("S1", S1, 1, 2750m)),
            Form(4, bahceli, new DateTime(2026, 7, 20), "21259", Tl("S1", S1, 1, 2750m), Tl("S3", S3, 1, 750m)),
        };
        return (claim, forms);
    }

    private static IXLWorksheet BuildSheet(MainClaim claim, List<MainForm> forms, out XLWorkbook wb)
    {
        wb = new XLWorkbook();
        var ws = MaintenanceClaimExcelBuilder.Build(wb, claim, forms);
        wb.RecalculateAllFormulas();
        return ws;
    }

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1e-6, expected + 1e-6);

    [Fact]
    public void Excel_BaslikVeSutunlarOrnekTabloyla_Ayni()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            Assert.Equal("MALZ HAKEDIS", ws.Name);
            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA SERVİS VE BAKIM MALZEMELERİ HAKEDİŞİ", ws.Cell(1, 1).GetString());
            Assert.Equal(new[]
            {
                "SIRA NO", "MAĞAZA KODU", "MAĞAZA ADI", "FORMAT", "TARİH", "BAKIM FORM NO", "MALZEME KODU", "MALZEME ADI", "MALZEME TİPİ",
                "MİKTARI", "BİRİMİ", "FİYAT", "TOPLAM", "GENEL TOPLAM",
            }, Enumerable.Range(1, 14).Select(c => ws.Cell(2, c).GetString()).ToArray());
            Assert.True(ws.Row(3).Cells(1, 14).All(c => c.IsEmpty()));   // yeşil ayırıcı satır
        }
    }

    [Fact]
    public void Excel_HerKalemBirSatir_MagazaKoduSonraTarihSirasiyla()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            // 386'nın 10 kalemi (tarih: 20 → 23 → 24 → 25 Temmuz), sonra 390'ın 1 kalemi
            Assert.Equal(Enumerable.Range(1, 11).ToArray(), Enumerable.Range(4, 11).Select(r => (int)ws.Cell(r, 1).GetDouble()).ToArray());
            Assert.Equal(new[] { 386, 386, 386, 386, 386, 386, 386, 386, 386, 386, 390 },
                Enumerable.Range(4, 11).Select(r => (int)ws.Cell(r, 2).GetDouble()).ToArray());
            Assert.Equal(new[] { 21259, 21259, 21169, 21179, 21179, 21179, 21179, 21179, 21183, 21183, 21140 },
                Enumerable.Range(4, 11).Select(r => (int)ws.Cell(r, 6).GetDouble()).ToArray());
            Assert.Equal(new[] { "S1", "S3", "S1", "S1", "S3", "35", "36", "61", "S1", "35", "S1" },
                Enumerable.Range(4, 11).Select(r => ws.Cell(r, 7).GetString()).ToArray());
            Assert.Equal(new DateTime(2026, 7, 24), ws.Cell(7, 5).GetDateTime());
            Assert.Equal("BAHÇELİEVLER ANKARA MM MİGROS", ws.Cell(4, 3).GetString());
            Assert.Equal("SERVİS REYONLU M", ws.Cell(14, 4).GetString());
        }
    }

    [Fact]
    public void Excel_MalzemeAdiVeTipiAyriSutunda_TipiOlmayandaHucreBos()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            // 21179 formu: 7..11. satırlar
            Assert.Equal(S1, ws.Cell(7, 8).GetString());
            Assert.True(ws.Cell(7, 9).IsEmpty());                              // servis bedelinde tip yok → BOŞ (0 değil)
            Assert.Equal(S3, ws.Cell(8, 8).GetString());
            Assert.True(ws.Cell(8, 9).IsEmpty());
            Assert.Equal("EXPANSİON VALVE", ws.Cell(9, 8).GetString());
            Assert.Equal("DIŞ DENGELİ VE KAYNAKLI", ws.Cell(9, 9).GetString());
            Assert.Equal("ORFİS", ws.Cell(10, 8).GetString());
            Assert.True(ws.Cell(10, 9).IsEmpty());
            Assert.Equal("CAREL FAN HIZ KONTROL MONOFAZE", ws.Cell(11, 8).GetString());
            Assert.Equal("FCSM122300 12 AMPER MONO", ws.Cell(11, 9).GetString());
            Assert.Equal("set", ws.Cell(7, 11).GetString());
            Assert.Equal("ADET", ws.Cell(9, 11).GetString());
            Assert.Equal(8, ws.Cell(8, 10).GetDouble());
        }
    }

    [Fact]
    public void Excel_FiyatToplamVeGenelToplamOrnekTablodakiRakamlarlaAyni()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            // Excel'in gösterdiği 2 haneli değer (15 anlamlı basamak üzerinden yuvarlar: 5195,775 → 5.195,78)
            static double Disp(IXLCell c) => (double)Math.Round((decimal)c.GetDouble(), 2, MidpointRounding.AwayFromZero);

            // FİYAT: EUR × kur (yuvarlanmadan) — örnek tabloda 3.173,93 / 514,57 / 5.195,78
            Assert.Equal(3173.93, Disp(ws.Cell(9, 12)));
            Assert.Equal(514.57, Disp(ws.Cell(10, 12)));
            Assert.Equal(5195.78, Disp(ws.Cell(11, 12)));
            Assert.Equal(2750.00, Disp(ws.Cell(7, 12)));

            // TOPLAM = miktar × fiyat — 6.000,00 / 6.347,85 / 1.029,14 / 5.195,78
            Assert.Equal(6000.00, Disp(ws.Cell(8, 13)));
            Assert.Equal(6347.85, Disp(ws.Cell(9, 13)));
            Assert.Equal(1029.14, Disp(ws.Cell(10, 13)));
            Assert.Equal(5195.78, Disp(ws.Cell(11, 13)));
            Assert.Equal("J9*L9", ws.Cell(9, 13).FormulaA1);

            // GENEL TOPLAM: 386 bloğu (10 satır birleşik) 33.496,69 — 390 tek satır 2.750,00
            Assert.True(ws.Range(4, 14, 13, 14).Cells().All(c => c.IsMerged()));
            Near(33496.688745, ws.Cell(4, 14).GetDouble());
            Assert.Equal(33496.69, Disp(ws.Cell(4, 14)));
            Assert.Equal(2750.00, Disp(ws.Cell(14, 14)));

            // Alt toplam satırı (bir boş satırdan sonra)
            Assert.True(ws.Row(15).Cells(1, 14).All(c => c.IsEmpty()));
            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA SERVİS VE BAKIM MALZEMELERİ HAKEDİŞİ", ws.Cell(16, 1).GetString());
            Assert.Equal(36246.69, Disp(ws.Cell(16, 14)));
        }
    }

    [Fact]
    public void Excel_GenelIcmalIlkSayfa_CalismaVeMagazalarSayfalariYok()
    {
        var (claim, forms) = ScreenshotData();
        BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            Assert.Equal(new[] { "GENEL ICMAL", "MALZ HAKEDIS" }, wb.Worksheets.OrderBy(w => w.Position).Select(w => w.Name).ToArray());

            var icmal = wb.Worksheet(1);
            Assert.Equal("Temmuz 26", icmal.Cell(1, 1).GetString());
            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA SERVİS VE BAKIM MALZEMELERİ İCMALİ", icmal.Cell(2, 1).GetString());
            Assert.Equal(new[] { "S/N", "MAĞAZA KODU", "MAĞAZA ADI", "İL", "FORMAT", "TOPLAM FİYAT" },
                Enumerable.Range(1, 6).Select(c => icmal.Cell(3, c).GetString()).ToArray());

            Assert.Equal(386, (int)icmal.Cell(4, 2).GetDouble());
            Assert.Equal("BAHÇELİEVLER ANKARA MM MİGROS", icmal.Cell(4, 3).GetString());
            Assert.Contains("'MALZ HAKEDIS'!B4", icmal.Cell(4, 3).FormulaA1);
            Assert.Equal("ANKARA", icmal.Cell(4, 4).GetString());
            Assert.Equal("MM", icmal.Cell(4, 5).GetString());
            Near(33496.688745, icmal.Cell(4, 6).GetDouble());
            Assert.Equal(390, (int)icmal.Cell(5, 2).GetDouble());
            Assert.Contains("'MALZ HAKEDIS'!B14", icmal.Cell(5, 3).FormulaA1);
            Near(2750, icmal.Cell(5, 6).GetDouble());
            Assert.Equal("GENEL TOPLAM :", icmal.Cell(7, 1).GetString());
            Near(36246.688745, icmal.Cell(7, 6).GetDouble());
        }
    }

    /// <summary>Ekranda kalem "KATEGORİ - ad tip" biçiminde görünür (fatura açıklaması); çıktıda MALZEME ADI kategori öneksiz
    /// ve tipsiz temiz ad olmalı.</summary>
    [Fact]
    public void Excel_FiyatListesindenEklenenKalemde_KategoriOnekiVeTipAdaKarismaz()
    {
        var (claim, forms) = ScreenshotData();
        var valve = Eur("35", "GENLEŞME VANASI - EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", "DIŞ DENGELİ VE KAYNAKLI", 1, 59.5595m);
        valve.ExportDescription = MaintenanceClaim.ExportName("EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", "DIŞ DENGELİ VE KAYNAKLI");
        var labor = Tl("S3", "İŞÇİLİKLER - ADAM SAAT GUNDUZ /GECE (>=2. GUN) ADAM SAAT GUNDUZ /GECE (>=2. GUN)", 2, 750m);
        labor.ExportDescription = MaintenanceClaim.ExportName("ADAM SAAT GUNDUZ /GECE (>=2. GUN) ADAM SAAT GUNDUZ /GECE (>=2. GUN)", null);
        forms = new List<MainForm> { Form(1, forms[0].Store!, new DateTime(2026, 7, 14), "21140", Tl("S1", S1, 1, 2750m), labor, valve) };

        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            Assert.Equal(S3, ws.Cell(5, 8).GetString());
            Assert.True(ws.Cell(5, 9).IsEmpty());
            Assert.Equal("EXPANSİON VALVE", ws.Cell(6, 8).GetString());
            Assert.Equal("DIŞ DENGELİ VE KAYNAKLI", ws.Cell(6, 9).GetString());
        }
    }

    [Fact]
    public void Excel_EksikFormlarAyriSayfadaListelenir()
    {
        var (claim, forms) = ScreenshotData();
        forms.Add(new MainForm { Id = 9, OriginalFileName = "magazasiz.pdf", ServiceItems = new List<MainItem> { Tl("S1", S1, 1, 2750m) } });
        forms.Add(Form(10, forms[0].Store!, new DateTime(2026, 7, 30), "21999",
            Eur("36", "ORFİS", null, 1, 9.656m)));   // servis bedeli silinmiş
        BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            var missing = wb.Worksheet("Eksik Formlar");
            var text = string.Join("|", missing.CellsUsed().Select(c => c.GetString()));
            Assert.Contains("magazasiz.pdf", text);
            Assert.Contains("mağaza", text);
            Assert.Contains("servis bedeli", text);
        }
    }

    // ---------------------------------------------------------------- fiyat listesi seed'i (birim, tip, S kodları)
    [Fact]
    public async Task Seed_FiyatListesindeBirimTipVeIscilikKodlariGelir_AciklamaDegismez()
    {
        using var db = CreateDb();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "HakedisOtomasyon.Web", "SeedData", CoolingSeedService.PriceFileName))) dir = dir.Parent;
        var seedDir = Path.Combine(dir!.FullName, "src", "HakedisOtomasyon.Web", "SeedData");

        await CoolingSeedService.SeedIfNeededAsync(db, seedDir);

        var items = await db.PriceItems.Where(p => p.Discipline == MechanicalDiscipline.Cooling).ToListAsync();
        var s1 = items.Single(p => p.PozNo == "S1");
        Assert.StartsWith("1 EKIP ŞEHİR İÇİ SERVİS BEDELİ", s1.Description);
        Assert.Equal("set", s1.Unit);
        Assert.Null(s1.ItemType);
        Assert.Equal(2750m, s1.MaterialPrice);
        var s2 = items.Single(p => p.PozNo == "S2");
        Assert.StartsWith("1 EKIP ŞEHİR DIŞI SERVİS BEDELİ", s2.Description);
        Assert.Equal(4200m, s2.MaterialPrice);
        Assert.Equal(750m, items.Single(p => p.PozNo == "S3").MaterialPrice);

        var valve = items.Single(p => p.PozNo == "35");
        Assert.Equal("EXPANSİON VALVE DIŞ DENGELİ VE KAYNAKLI", valve.Description);   // açıklama (ad + tip) değişmedi
        Assert.Equal("DIŞ DENGELİ VE KAYNAKLI", valve.ItemType);
        Assert.Equal("ADET", valve.Unit);
        Assert.False(valve.HasMissingUnit);
        Assert.Equal(59.5595m, valve.ForeignPrice);

        // fiyat listesinden servis bedeli bulunur ve forma uygulanır
        var dtos = await new PriceListService(db, null!, new CurrentDisciplineService { CurrentDiscipline = MechanicalDiscipline.Cooling }).GetAllAsync();
        var formItems = new List<ServiceItemDto>();
        var result = MaintenanceClaim.ApplyServiceFee(formItems, "ZONGULDAK",
            MaintenanceClaim.FindFee(dtos, true), MaintenanceClaim.FindFee(dtos, false));
        Assert.Equal(MaintenanceClaim.ServiceFeeResult.Added, result);
        Assert.Equal("S2", formItems[0].ItemCode);
        Assert.Equal(4200m, formItems[0].TotalPrice);
    }

    /// <summary>Önceki sürümle (CoolingPricesV2) kurulmuş veritabanında: kodlar S1..S6 olur, tip ve boş birimler dolar; kullanıcının
    /// girdiği birim korunur; kalem sayısı değişmez (silinip yeniden eklenmez).</summary>
    [Fact]
    public async Task Seed_OncekiSurumdenGecis_KodTipBirimGuncellenir_KullaniciBirimiKorunur()
    {
        using var db = CreateDb();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "HakedisOtomasyon.Web", "SeedData", CoolingSeedService.PriceFileName))) dir = dir.Parent;
        var seedDir = Path.Combine(dir!.FullName, "src", "HakedisOtomasyon.Web", "SeedData");
        await CoolingSeedService.SeedIfNeededAsync(db, seedDir);

        // Eski sürümdeki durum: kod 247, birim boş, tip yok; kullanıcı bir kaleme kendi birimini girmiş
        var s1 = await db.PriceItems.SingleAsync(p => p.PozNo == "S1");
        var s1Id = s1.Id;
        s1.PozNo = "247"; s1.Unit = string.Empty; s1.HasMissingUnit = true; s1.ItemType = null;
        var valve = await db.PriceItems.SingleAsync(p => p.PozNo == "35");
        valve.Unit = "Ad"; valve.ItemType = null;
        db.AppSettings.RemoveRange(db.AppSettings.Where(a => a.Key == CoolingSeedService.PriceRefreshFlagKey));
        await db.SaveChangesAsync();
        var countBefore = await db.PriceItems.CountAsync();

        var result = await CoolingSeedService.SeedIfNeededAsync(db, seedDir);

        Assert.Equal(0, result.PriceItemsAdded);
        Assert.Equal(0, result.PriceItemsRemoved);
        Assert.Equal(countBefore, await db.PriceItems.CountAsync());
        var s1After = await db.PriceItems.SingleAsync(p => p.Id == s1Id);
        Assert.Equal("S1", s1After.PozNo);
        Assert.Equal("set", s1After.Unit);
        Assert.False(s1After.HasMissingUnit);
        var valveAfter = await db.PriceItems.SingleAsync(p => p.PozNo == "35");
        Assert.Equal("Ad", valveAfter.Unit);                              // kullanıcının birimi korundu
        Assert.Equal("DIŞ DENGELİ VE KAYNAKLI", valveAfter.ItemType);     // tip doldu
    }
}
