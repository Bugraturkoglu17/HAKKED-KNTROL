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
using MainPriceItem = HakedisOtomasyon.Domain.Entities.PriceItem;
using MainStore = HakedisOtomasyon.Domain.Entities.Store;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>Soğutma Glikol Kullanım hakedişi: servis ücreti yok, glikol kalemi otomatik, kullanıcı miktar/tarih/form no/mağaza girer,
/// Excel çıktısı onaylı örnek tabloyla (6 form, 5 mağaza, genel toplam 71.127,87) birebir aynı olmalı.</summary>
public class GlycolClaimTests
{
    private const decimal Rate = 53.987m;
    private const decimal GlycolEur = 8.5m;

    private static MainDb CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glycol_test_{Guid.NewGuid():N}.db");
        var db = new MainDb(new DbContextOptionsBuilder<MainDb>().UseSqlite($"Data Source={path}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    // ---------------------------------------------------------------- yardımcı: gerçek örnek tablo verisi
    private static MainStore Store(string code, string name, string format, string region = "İÇ ANADOLU", string? city = "ANKARA") =>
        new() { Code = code, Name = name, Format = format, Region = region, City = city, Discipline = MechanicalDiscipline.Cooling };

    private static MainItem GlycolItem(decimal qty) => new()
    {
        ItemCode = "171", Description = "İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL", Quantity = qty, Unit = "Ad",
        CurrencyCode = "EUR", ForeignPrice = GlycolEur, ExchangeRate = Rate, ExchangeRateDate = new DateTime(2026, 7, 31),
        ExchangeRateSource = "TCMB", MaterialPrice = EurPricing.ToTl(GlycolEur, Rate), UnitPrice = EurPricing.ToTl(GlycolEur, Rate),
        TotalPrice = EurPricing.TotalTl(qty, GlycolEur, Rate), SortOrder = 1,
    };

    private static MainForm Form(int id, MainStore? store, string? no, DateTime? date, decimal qty) => new()
    {
        Id = id, OriginalFileName = $"form{id}.pdf", Store = store, StoreId = store?.Id, FormNumber = no, FormDate = date,
        ServiceItems = new List<MainItem> { GlycolItem(qty) },
    };

    private static (MainClaim claim, List<MainForm> forms) ScreenshotData()
    {
        var goksupark = Store("1761", "GÖKSUPARK MMM MİGROS", "MMM");
        var bartin = Store("3239", "BARTIN MMM MİGROS", "MMM");
        var cekerek = Store("7796", "ÇEKEREK YOZGAT M MİGROS", "M ANADOLU");
        var karabuk = Store("8039", "MJET 5000 EVLER KARABÜK", "M-JET");
        var kirsehir = Store("8696", "ZÜBEYDE HANIM CD KIRŞEHİR MM", "MM");
        var claim = new MainClaim
        {
            Id = 1, Name = "Haziran Glikol", CompanyName = "İNTİKOŞ", Month = 6, Year = 2026,
            Discipline = MechanicalDiscipline.Cooling, CoolingCategory = CoolingClaimCategory.GlycolUsage,
            EurRate = Rate, EurRateSource = "TCMB", EurRateDate = new DateTime(2026, 7, 31),
        };
        // BİLEREK karışık sırada — çıktı mağaza koduna göre sıralanmalı, aynı mağaza yan yana olmalı
        var forms = new List<MainForm>
        {
            Form(4, kirsehir, "4495", new DateTime(2026, 7, 5), 40),
            Form(1, goksupark, "5015", new DateTime(2026, 7, 15), 30),
            Form(5, karabuk, "5032", new DateTime(2026, 7, 21), 20),
            Form(3, cekerek, "4482", new DateTime(2026, 7, 1), 10),
            Form(2, bartin, "3410", new DateTime(2026, 7, 31), 35),
            Form(6, goksupark, "5016", new DateTime(2026, 7, 15), 20),
        };
        return (claim, forms);
    }

    private static IXLWorksheet BuildSheet(MainClaim claim, List<MainForm> forms, out XLWorkbook wb)
    {
        wb = new XLWorkbook();
        return GlycolClaimExcelBuilder.Build(wb, claim, forms);
    }

    // ---------------------------------------------------------------- Excel çıktısı
    /// <summary>Kuruşun çok altında tolerans — yuvarlama sınırındaki (…,xx5) değerlerde hane karşılaştırması yanıltır.</summary>
    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1e-6, expected + 1e-6);

    [Fact]
    public void Excel_BaslikSatiriOrnekTabloyla_AyniSutunlarVeSirada()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            var headers = Enumerable.Range(1, 14).Select(c => ws.Cell(1, c).GetString()).Where(h => h.Length > 0).ToArray();
            Assert.Equal(new[]
            {
                "MAĞAZA KODU", "MAĞAZA ADI", "FORMAT", "TARİH", "FORM NO", "MALZEME KODU", "MALZEME ADI",
                "ONAYA SUNULAN MİKTAR", "KESİLEN MİKTAR", "FATURA DÖNEMİ", "FİYAT", "TOPLAM", "GENEL TOPLAM",
            }, headers);   // ONAYLANAN MİKTAR sütunu yok — kesilen doğrudan GENEL TOPLAM'dan düşer
        }
    }

    [Fact]
    public void Excel_MagazalarKodaGoreSiraliVeAyniMagazaYanYana()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            var codes = Enumerable.Range(2, 6).Select(r => (int)ws.Cell(r, 1).GetDouble()).ToArray();
            Assert.Equal(new[] { 1761, 1761, 3239, 7796, 8039, 8696 }, codes);
            // aynı mağazanın iki formu tarih/form no sırasıyla
            Assert.Equal(5015, (int)ws.Cell(2, 5).GetDouble());
            Assert.Equal(5016, (int)ws.Cell(3, 5).GetDouble());
            Assert.Equal("GÖKSUPARK MMM MİGROS", ws.Cell(2, 2).GetString());
            Assert.Equal("M ANADOLU", ws.Cell(5, 3).GetString());
            Assert.Equal("M-JET", ws.Cell(6, 3).GetString());
            Assert.Equal(new DateTime(2026, 7, 15), ws.Cell(2, 4).GetDateTime());
        }
    }

    [Fact]
    public void Excel_MalzemeKoduFaturaDonemiVeFiyatOtomatik_KesilenBos()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            for (int r = 2; r <= 7; r++)
            {
                Assert.Equal(171, (int)ws.Cell(r, 6).GetDouble());                       // MALZEME KODU
                Assert.Equal("İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL", ws.Cell(r, 7).GetString()); // MALZEME ADI
                Assert.Equal("HAZİRAN", ws.Cell(r, 10).GetString());                      // FATURA DÖNEMİ (hakediş ayı)
                Assert.True(ws.Cell(r, 9).IsEmpty());                                     // KESİLEN MİKTAR boş
                Assert.Equal(458.8895, ws.Cell(r, 11).GetDouble(), 4);                    // FİYAT = 8,5 × 53,987 (yuvarlanmadan)
            }
        }
    }

    [Fact]
    public void Excel_ToplamlarOrnekTablodakiRakamlarlaAyni()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            static double Disp(IXLCell c) => Math.Round(c.GetDouble(), 2, MidpointRounding.AwayFromZero);

            // TOPLAM: örnek tabloda 13.766,69 / 9.177,79 / 16.061,13 / 4.588,90 / 9.177,79 / 18.355,58
            Assert.Equal(new[] { 13766.69, 9177.79, 16061.13, 4588.90, 9177.79, 18355.58 },
                Enumerable.Range(2, 6).Select(r => Disp(ws.Cell(r, 12))).ToArray());

            // GENEL TOPLAM (mağaza bloğu): 1761 → 22.944,48 (iki satır birleşik); diğerleri tek satır
            Assert.True(ws.Range(2, 13, 3, 13).Cells().All(c => c.IsMerged()));
            Assert.Equal(22944.48, Disp(ws.Cell(2, 13)));
            Assert.Equal(16061.13, Disp(ws.Cell(4, 13)));
            Assert.Equal(4588.90, Disp(ws.Cell(5, 13)));
            Assert.Equal(9177.79, Disp(ws.Cell(6, 13)));
            Assert.Equal(18355.58, Disp(ws.Cell(7, 13)));
            // ONAYLANAN MİKTAR sütunu yok
            Assert.True(ws.Column(14).IsEmpty());

            // Alt satır: 71.127,87 (satırlar yuvarlanmadan toplanır — yuvarlanıp toplansa 71.127,88 olurdu)
            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA GLİKOL KULLANIM HAKEDİŞİ", ws.Cell(8, 1).GetString());
            Assert.Equal(71127.87, Disp(ws.Cell(8, 13)));
        }
    }

    [Fact]
    public void Excel_KesilenMiktarDogrudanGenelToplamdanDuser()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            ws.Cell(2, 9).Value = 5;   // 5 birim kesildi (5015 numaralı form)
            wb.RecalculateAllFormulas();

            var expected = 22944.475 - 5 * 458.8895;
            Near(expected, ws.Cell(2, 13).GetDouble());                // GENEL TOPLAM düştü
            Near(30 * 458.8895, ws.Cell(2, 12).GetDouble());           // TOPLAM (miktar × fiyat) değişmez
            Near(16061.1325, ws.Cell(4, 13).GetDouble());              // kesilmeyen mağaza aynı
            Near(71127.8725 - 5 * 458.8895, ws.Cell(8, 13).GetDouble()); // alt toplam da düşer

            // icmal sayfası aynı hücrelere bağlı olduğundan o da güncellenir
            var icmal = wb.Worksheet(GlycolClaimExcelBuilder.SummarySheetName);
            Near(expected, icmal.Cell(4, 6).GetDouble());
            Near(71127.8725 - 5 * 458.8895, icmal.Cell(10, 6).GetDouble());
        }
    }

    /// <summary>Kullanıcının örneği: 20 adet × 450,50 = 9.010,00; 5 kesilince GENEL TOPLAM 6.757,50, tablo toplamı 51.807,50.</summary>
    [Fact]
    public void Excel_KesilenMiktar_KullaniciOrnegi_6757_50_ve_51807_50()
    {
        var claim = new MainClaim { Id = 1, Name = "Temmuz Glikol", CompanyName = "OKUR", Month = 7, Year = 2026,
            Discipline = MechanicalDiscipline.Cooling, CoolingCategory = CoolingClaimCategory.GlycolUsage, EurRate = 53m };
        var forms = Enumerable.Range(1, 6).Select(i =>
        {
            var f = Form(i, Store((3000 + i).ToString(), $"MAĞAZA {i}", "MM"), (5000 + i).ToString(), new DateTime(2026, 7, i), 20);
            var item = f.ServiceItems.Single();
            item.ForeignPrice = 8.5m; item.ExchangeRate = 53m; item.UnitPrice = 450.50m;   // 8,5 × 53 = 450,50
            return f;
        }).ToList();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            ws.Cell(2, 9).Value = 5;
            wb.RecalculateAllFormulas();

            Assert.Equal("TEMMUZ", ws.Cell(2, 10).GetString());
            Assert.Equal(450.50, ws.Cell(2, 11).GetDouble(), 4);
            Assert.Equal(9010.00, ws.Cell(2, 12).GetDouble(), 4);
            Assert.Equal(6757.50, ws.Cell(2, 13).GetDouble(), 4);
            Assert.Equal(9010.00, ws.Cell(3, 13).GetDouble(), 4);
            Assert.Equal(51807.50, ws.Cell(8, 13).GetDouble(), 4);
        }
    }

    [Fact]
    public void Excel_GenelIcmalSayfasi_IlkSayfa_MagazaBasinaBirSatirVeGenelToplam()
    {
        var (claim, forms) = ScreenshotData();
        forms.Single(f => f.Store!.Code == "3239").Store!.City = "BARTIN";
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            wb.RecalculateAllFormulas();
            var icmal = wb.Worksheet(1);
            Assert.Equal("GENEL ICMAL", icmal.Name);
            Assert.Equal("MALZ HAKEDIS", ws.Name);
            Assert.Equal(2, ws.Position);

            Assert.Equal("Haziran 26", icmal.Cell(1, 1).GetString());
            Assert.Equal("İÇ ANADOLU MİGROS SATIŞ MAĞAZALARI SOĞUTMA GLİKOL KULLANIM İCMALİ", icmal.Cell(2, 1).GetString());
            Assert.Equal(new[] { "S/N", "MAĞAZA KODU", "MAĞAZA ADI", "İL", "FORMAT", "TOPLAM FİYAT" },
                Enumerable.Range(1, 6).Select(c => icmal.Cell(3, c).GetString()).ToArray());

            // 5 mağaza (1761'in iki formu TEK satır), mağaza koduna göre sıralı
            Assert.Equal(new[] { 1761, 3239, 7796, 8039, 8696 }, Enumerable.Range(4, 5).Select(r => (int)icmal.Cell(r, 2).GetDouble()).ToArray());
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Enumerable.Range(4, 5).Select(r => (int)icmal.Cell(r, 1).GetDouble()).ToArray());
            Assert.Equal("GÖKSUPARK MMM MİGROS", icmal.Cell(4, 3).GetString());
            Assert.Contains("HYPERLINK", icmal.Cell(4, 3).FormulaA1);
            Assert.Contains("'MALZ HAKEDIS'!A2", icmal.Cell(4, 3).FormulaA1);
            Assert.Contains("'MALZ HAKEDIS'!A4", icmal.Cell(5, 3).FormulaA1);   // 3239 bloğu 4. satırda başlar
            Assert.Equal("ANKARA", icmal.Cell(4, 4).GetString());
            Assert.Equal("BARTIN", icmal.Cell(5, 4).GetString());
            Assert.Equal("M ANADOLU", icmal.Cell(6, 5).GetString());

            static double Disp(IXLCell c) => Math.Round(c.GetDouble(), 2, MidpointRounding.AwayFromZero);
            Assert.Equal(new[] { 22944.48, 16061.13, 4588.90, 9177.79, 18355.58 },
                Enumerable.Range(4, 5).Select(r => Disp(icmal.Cell(r, 6))).ToArray());

            Assert.True(icmal.Row(9).IsEmpty());                                  // boş ayırıcı satır
            Assert.Equal("GENEL TOPLAM :", icmal.Cell(10, 1).GetString());
            Assert.Equal(71127.87, Disp(icmal.Cell(10, 6)));
        }
    }

    [Fact]
    public void Excel_ServisUcretiSatiriYok_HerFormTekSatir()
    {
        var (claim, forms) = ScreenshotData();
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            var text = string.Join("|", ws.CellsUsed().Select(c => c.GetString())).ToUpper(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
            Assert.DoesNotContain("SERVİS ÜCRETİ", text);
            // 1 başlık + 6 form satırı (her form TEK satır) + alt toplam (8) + kur notu (10)
            Assert.Equal(10, ws.LastRowUsed()!.RowNumber());
        }
    }

    [Fact]
    public void Excel_MagazasiOlmayanForm_AnaTabloyaGirmezEksikFormlardaListelenir()
    {
        var (claim, forms) = ScreenshotData();
        forms.Add(Form(9, null, "9999", new DateTime(2026, 7, 2), 5));
        var ws = BuildSheet(claim, forms, out var wb);
        using (wb)
        {
            // mağazasız form ana tabloya girmez: alt toplam hâlâ 8. satırda (6 form satırı)
            Assert.Contains("GLİKOL KULLANIM HAKEDİŞİ", ws.Cell(8, 1).GetString());
            var missing = wb.Worksheet("Eksik Formlar");
            Assert.Equal("form9.pdf", missing.Cell(2, 1).GetString());
            Assert.Contains("mağaza", missing.Cell(2, 3).GetString());
        }
    }

    // ---------------------------------------------------------------- yardımcılar
    [Fact]
    public void FaturaDonemi_HakedisAyininTurkceBuyukHarfliAdi()
    {
        Assert.Equal("HAZİRAN", GlycolClaim.PeriodName(6));
        Assert.Equal("OCAK", GlycolClaim.PeriodName(1));
        Assert.Equal("EKİM", GlycolClaim.PeriodName(10));
        Assert.Equal(string.Empty, GlycolClaim.PeriodName(13));
    }

    [Theory]
    [InlineData("İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL GLİKOL", "İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL")]
    [InlineData("İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL", "İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL")]
    [InlineData("  Fan   Fan ", "Fan")]
    public void CleanItemName_SondakiTekrariAtar(string input, string expected)
        => Assert.Equal(expected, GlycolClaim.CleanItemName(input));

    [Fact]
    public void MagazaKodlariSayisalSiralanir_BosKodSona()
    {
        var codes = new[] { "10227", "1761", "300", null, "A12", "8039" };
        var sorted = codes.OrderBy(c => c, Comparer<string?>.Create(GlycolClaim.CompareStoreCodes)).ToArray();
        Assert.Equal(new[] { "300", "1761", "8039", "10227", "A12", null }, sorted);
    }

    // ---------------------------------------------------------------- servis (veritabanı)
    private static async Task<(ServiceFormService svc, MainClaim claim, MainStore store)> SeedAsync(MainDb db, bool glycol = true)
    {
        db.PriceItems.Add(new MainPriceItem
        {
            Description = "İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL GLİKOL", PozNo = "171", Discipline = MechanicalDiscipline.Cooling, IsActive = true,
            IsCurrencyBased = true, CurrencyCode = "EUR", ForeignPrice = GlycolEur, Unit = string.Empty,
        });
        var store = Store("1761", "GÖKSUPARK MMM MİGROS", "MMM");
        db.Stores.Add(store);
        var claim = new MainClaim
        {
            Name = "Haziran Glikol", CompanyName = "İNTİKOŞ", Month = 6, Year = 2026, Discipline = MechanicalDiscipline.Cooling,
            CoolingCategory = glycol ? CoolingClaimCategory.GlycolUsage : CoolingClaimCategory.Monitoring,   // kullanım (Glikol/Gaz) hakedişi OLMAYAN bir tür
            EurRate = Rate, EurRateSource = "TCMB", EurRateDate = new DateTime(2026, 7, 31),
        };
        db.ProgressClaims.Add(claim);
        await db.SaveChangesAsync();
        return (new ServiceFormService(db, new FakeFileStorage(), null!, null!), claim, store);
    }

    [Fact]
    public async Task FormEklenince_GlikolHakedisindeServisUcretiEklenmez()
    {
        using var db = CreateDb();
        var (svc, claim, _) = await SeedAsync(db);

        var form = await svc.AddFormAsync(claim.Id, "forms/x.pdf", "x.pdf");

        Assert.DoesNotContain(form.ServiceItems, i => i.Description == "Servis Ücreti");
        Assert.Empty(await db.ServiceItems.Where(i => i.ServiceFormId == form.Id).ToListAsync());
    }

    [Fact]
    public async Task EnsureGlycolItems_HerFormaGlikolKalemiOtomatikEklenirServisUcretiKalkar()
    {
        using var db = CreateDb();
        var (svc, claim, _) = await SeedAsync(db);
        var f1 = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        var f2 = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "b.pdf" };
        db.ServiceForms.AddRange(f1, f2);
        await db.SaveChangesAsync();
        // Eski akıştan kalma otomatik servis ücreti
        db.ServiceItems.Add(new MainItem { ServiceFormId = f1.Id, Description = "Servis Ücreti", LaborPrice = 4550m, UnitPrice = 4550m, TotalPrice = 4550m });
        await db.SaveChangesAsync();

        await svc.EnsureGlycolItemsAsync(claim.Id);

        foreach (var form in new[] { f1, f2 })
        {
            var items = await db.ServiceItems.Where(i => i.ServiceFormId == form.Id).ToListAsync();
            var glycol = Assert.Single(items);
            Assert.Equal("171", glycol.ItemCode);
            Assert.Equal("İNHİBİTÖRLÜ MONOPROPİLEN GLİKOL", glycol.Description);   // tekrarlanan "GLİKOL" atıldı
            Assert.Equal(0m, glycol.Quantity);                                       // miktarı kullanıcı girer
            Assert.Equal(458.89m, glycol.UnitPrice);                                 // 8,5 EUR × 53,987
            Assert.Equal("EUR", glycol.CurrencyCode);
            Assert.Equal(0m, glycol.LaborPrice);
        }
    }

    [Fact]
    public async Task EnsureGlycolItems_TekrarCalisinca_KopyaEklemezMiktariKorur()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();
        await svc.EnsureGlycolItemsAsync(claim.Id);
        await svc.SaveGlycolRowAsync(form.Id, store.Id, new DateTime(2026, 7, 15), "5015", 30m);

        await svc.EnsureGlycolItemsAsync(claim.Id);

        var glycol = Assert.Single(await db.ServiceItems.Where(i => i.ServiceFormId == form.Id).ToListAsync());
        Assert.Equal(30m, glycol.Quantity);
    }

    [Fact]
    public async Task SaveGlycolRow_EksiksizseTamamlandiOlurToplamEurKurdanHesaplanir()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();
        await svc.EnsureGlycolItemsAsync(claim.Id);

        var dto = await svc.SaveGlycolRowAsync(form.Id, store.Id, new DateTime(2026, 7, 15), " 5015 ", 30m);

        Assert.Equal(ServiceFormStatus.Tamamlandi, dto.Status);
        Assert.Equal("5015", dto.FormNumber);
        Assert.Equal(new DateTime(2026, 7, 15), dto.FormDate);
        Assert.Equal("1761", dto.StoreCode);
        Assert.Equal("MMM", dto.StoreFormat);
        Assert.Equal("İÇ ANADOLU", dto.StoreRegion);
        var item = Assert.Single(dto.ServiceItems);
        Assert.Equal(30m, item.Quantity);
        Assert.Equal(13766.69m, item.TotalPrice);   // 30 × 8,5 × 53,987 = 13.766,685 → 13.766,69 (birim fiyat 458,89 ile 13.766,70 DEĞİL)
    }

    [Theory]
    [InlineData(false, "5015", 30)]   // mağaza yok
    [InlineData(true, null, 30)]      // form no yok
    [InlineData(true, "  ", 30)]
    [InlineData(true, "5015", 0)]     // miktar yok
    public async Task SaveGlycolRow_EksikBilgiVarsaTamamlandiOlmaz(bool withStore, string? formNo, int qty)
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();

        var dto = await svc.SaveGlycolRowAsync(form.Id, withStore ? store.Id : null, new DateTime(2026, 7, 15), formNo, qty);

        Assert.Equal(ServiceFormStatus.EksikBilgi, dto.Status);
    }

    [Fact]
    public async Task SaveGlycolRow_TarihYoksaTamamlandiOlmaz()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();

        var dto = await svc.SaveGlycolRowAsync(form.Id, store.Id, null, "5015", 30m);

        Assert.Equal(ServiceFormStatus.EksikBilgi, dto.Status);
    }

    [Fact]
    public async Task ClearGlycolRow_SatiriTablodanCikarirDosyaVeKalemKalir()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf", FilePath = "forms/a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();
        await svc.EnsureGlycolItemsAsync(claim.Id);
        var added = await svc.SaveGlycolRowAsync(form.Id, store.Id, new DateTime(2026, 7, 15), "5015", 30m);
        Assert.Equal(ServiceFormStatus.Tamamlandi, added.Status);

        var cleared = await svc.ClearGlycolRowAsync(form.Id);

        Assert.Equal(ServiceFormStatus.Bekliyor, cleared.Status);            // yeniden doldurulabilir
        Assert.Null(cleared.StoreId);
        Assert.Null(cleared.FormDate);
        Assert.Null(cleared.FormNumber);
        Assert.Equal("forms/a.pdf", cleared.FilePath);                        // yüklenen form silinmez
        var item = Assert.Single(cleared.ServiceItems);                       // glikol kalemi kalır, miktarı sıfırlanır
        Assert.Equal(0m, item.Quantity);
        Assert.Equal(0m, item.TotalPrice);
        Assert.Equal(458.89m, item.UnitPrice);
    }

    [Fact]
    public async Task ClearGlycolRow_SonraTekrarEklenebilir()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();
        await svc.EnsureGlycolItemsAsync(claim.Id);
        await svc.SaveGlycolRowAsync(form.Id, store.Id, new DateTime(2026, 7, 15), "5015", 30m);
        await svc.ClearGlycolRowAsync(form.Id);

        var again = await svc.SaveGlycolRowAsync(form.Id, store.Id, new DateTime(2026, 7, 16), "5099", 12.5m);

        Assert.Equal(ServiceFormStatus.Tamamlandi, again.Status);
        Assert.Equal(12.5m, Assert.Single(again.ServiceItems).Quantity);       // ondalık miktar
        Assert.Equal(EurPricing.TotalTl(12.5m, GlycolEur, Rate), again.ServiceItems.Single().TotalPrice);
    }

    [Fact]
    public async Task SaveGlycolRow_BaskaDisiplindekiMagazaSecilemez()
    {
        using var db = CreateDb();
        var (svc, claim, _) = await SeedAsync(db);
        var fireStore = new MainStore { Code = "1761", Name = "YANGIN 1761", Discipline = MechanicalDiscipline.Fire };
        db.Stores.Add(fireStore);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SaveGlycolRowAsync(form.Id, fireStore.Id, new DateTime(2026, 7, 15), "5015", 30m));
    }

    [Fact]
    public async Task UpdateStore_AyniKodluYanginVeSogutmaMagazasindaSogutmaSecilir()
    {
        using var db = CreateDb();
        var (svc, claim, coolingStore) = await SeedAsync(db);
        db.Stores.Add(new MainStore { Code = "1761", Name = "YANGIN 1761", Discipline = MechanicalDiscipline.Fire });
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();

        await svc.UpdateStoreAsync(form.Id, "1761");

        Assert.Equal(coolingStore.Id, (await db.ServiceForms.FindAsync(form.Id))!.StoreId);
    }

    [Fact]
    public async Task EurKuruDegisince_GlikolKalemleriYeniKurlaYenidenFiyatlanir()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "a.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();
        await svc.EnsureGlycolItemsAsync(claim.Id);
        await svc.SaveGlycolRowAsync(form.Id, store.Id, new DateTime(2026, 7, 15), "5015", 30m);

        var claimService = new ProgressClaimService(db, new CurrentDisciplineService { CurrentDiscipline = MechanicalDiscipline.Cooling });
        await claimService.UpdateEurRateAsync(claim.Id, 50m, new DateTime(2026, 8, 3), "Manuel");

        var item = await db.ServiceItems.SingleAsync(i => i.ServiceFormId == form.Id);
        Assert.Equal(425m, item.UnitPrice);            // 8,5 × 50
        Assert.Equal(12750m, item.TotalPrice);         // 30 × 425
    }

    [Fact]
    public async Task GlikolKalemiFiyatListesindeYoksa_AnlasilirHataVerir()
    {
        using var db = CreateDb();
        var (svc, claim, _) = await SeedAsync(db);
        db.PriceItems.RemoveRange(db.PriceItems);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EnsureGlycolItemsAsync(claim.Id));
        Assert.Contains("glikol", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlikolOlmayanHakedisteGlikolIslemiReddedilir()
    {
        using var db = CreateDb();
        var (svc, claim, _) = await SeedAsync(db, glycol: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EnsureGlycolItemsAsync(claim.Id));
    }

    // ---------------------------------------------------------------- bir dosyadan çok kayıt
    private static async Task<MainForm> UploadedFileAsync(MainDb db, ServiceFormService svc, MainClaim claim)
    {
        var form = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "coksayfa.pdf", FilePath = "forms/coksayfa.pdf" };
        db.ServiceForms.Add(form);
        await db.SaveChangesAsync();
        await svc.EnsureGlycolItemsAsync(claim.Id);
        return form;
    }

    [Fact]
    public async Task AddGlycolEntry_IlkKayitBosYerTutucuyuKullanir_SonrakilerAyniDosyayaYeniSatirEkler()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = await UploadedFileAsync(db, svc, claim);

        var first = await svc.AddGlycolEntryAsync(form.Id, store.Id, new DateTime(2026, 6, 3), "5001", 10m);
        var second = await svc.AddGlycolEntryAsync(form.Id, store.Id, new DateTime(2026, 6, 9), "5002", 20m);
        var third = await svc.AddGlycolEntryAsync(form.Id, store.Id, new DateTime(2026, 6, 12), "5003", 5m);

        Assert.Equal(form.Id, first.Id);                              // yer tutucu doldu, fazladan satır açılmadı
        Assert.NotEqual(first.Id, second.Id);
        var rows = await db.ServiceForms.Include(f => f.ServiceItems).Where(f => f.FilePath == "forms/coksayfa.pdf").ToListAsync();
        Assert.Equal(3, rows.Count);                                  // 3 kayıt, tek dosya yolu
        Assert.All(rows, r => Assert.Equal(ServiceFormStatus.Tamamlandi, r.Status));
        Assert.All(rows, r => Assert.Single(r.ServiceItems));         // her satırda yalnız glikol kalemi
        Assert.Equal(new[] { 5m, 10m, 20m }, rows.Select(r => r.ServiceItems.Single().Quantity).OrderBy(x => x).ToArray());
        Assert.Contains("5003", rows.Select(r => r.FormNumber));
        Assert.Equal(third.Id, rows.Single(r => r.FormNumber == "5003").Id);
    }

    [Fact]
    public async Task RemoveGlycolRow_BaskaKayitVarsaSatiriKaldirir_SonKayitsaBosaltirVeDosyayiTutar()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = await UploadedFileAsync(db, svc, claim);
        var a = await svc.AddGlycolEntryAsync(form.Id, store.Id, new DateTime(2026, 6, 3), "5001", 10m);
        var b = await svc.AddGlycolEntryAsync(form.Id, store.Id, new DateTime(2026, 6, 9), "5002", 20m);

        Assert.Null(await svc.RemoveGlycolRowAsync(b.Id));            // sibling var → satır tamamen kalktı
        Assert.False(await db.ServiceForms.AnyAsync(f => f.Id == b.Id));

        var last = await svc.RemoveGlycolRowAsync(a.Id);              // dosyanın son kaydı → boşalır, dosya kalır
        Assert.NotNull(last);
        Assert.Equal(ServiceFormStatus.Bekliyor, last!.Status);
        Assert.Null(last.FormNumber);
        Assert.Equal("forms/coksayfa.pdf", (await db.ServiceForms.FindAsync(a.Id))!.FilePath);
    }

    [Fact]
    public async Task DosyaSilinince_AyniDosyadanGirilenTumKayitlarKalkar()
    {
        using var db = CreateDb();
        var (svc, claim, store) = await SeedAsync(db);
        var form = await UploadedFileAsync(db, svc, claim);
        await svc.AddGlycolEntryAsync(form.Id, store.Id, new DateTime(2026, 6, 3), "5001", 10m);
        var other = await svc.AddGlycolEntryAsync(form.Id, store.Id, new DateTime(2026, 6, 9), "5002", 20m);

        try { await svc.DeleteAsync(form.Id); }
        catch (NullReferenceException) { /* test ortamında _cleanup yok; kardeş kayıtlar silme öncesinde kaldırılır */ }

        Assert.False(await db.ServiceForms.AnyAsync(f => f.Id == other.Id));
        Assert.False(await db.ServiceItems.AnyAsync(i => i.ServiceFormId == other.Id));
    }

    /// <summary>Silinen (arşivlenen) form, hakediş yeniden yüklenince ("Formları işle") listeye geri gelmemeli.</summary>
    [Fact]
    public async Task SilinenArsivlenmisForm_YenidenYuklemedeGeriGelmezVeSayimlaraGirmez()
    {
        using var db = CreateDb();
        var (svc, claim, _) = await SeedAsync(db);
        var keep = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "kalan.pdf", FilePath = "forms/kalan.pdf" };
        var removed = new MainForm { ProgressClaimId = claim.Id, OriginalFileName = "silinen.pdf", FilePath = "forms/silinen.pdf" };
        db.ServiceForms.AddRange(keep, removed);
        await db.SaveChangesAsync();
        removed.IsArchived = true;   // DeleteAsync → ArchiveRemovedServiceFormAsync bunu yapar
        await db.SaveChangesAsync();

        var forms = await svc.GetByClaimIdAsync(claim.Id);
        Assert.Equal(new[] { keep.Id }, forms.Select(f => f.Id).ToArray());

        var claimService = new ProgressClaimService(db, new CurrentDisciplineService { CurrentDiscipline = MechanicalDiscipline.Cooling });
        Assert.Equal(1, (await claimService.GetByIdAsync(claim.Id))!.FormCount);
        Assert.Equal(1, (await claimService.GetAllAsync()).Single().FormCount);
    }

    // ---------------------------------------------------------------- mağaza Format/Bölge (seed)
    [Fact]
    public async Task SeedMagazaBilgisi_MevcutMagazalarinBosFormatBolgesiDolar_AdVeKullaniciDegeriDegismez()
    {
        using var db = CreateDb();
        db.Stores.Add(new MainStore { Code = "1761", Name = "BENİM ADIM", Discipline = MechanicalDiscipline.Cooling });                    // eski yükleme: format/bölge yok
        db.Stores.Add(new MainStore { Code = "3239", Name = "BARTIN", Format = "ÖZEL", Discipline = MechanicalDiscipline.Cooling });      // kullanıcı formatı girmiş
        db.AppSettings.Add(new HakedisOtomasyon.Domain.Entities.AppSetting { Key = CoolingSeedService.SeedFlagKey, Value = "x" });        // mağazalar zaten yüklenmişti
        await db.SaveChangesAsync();

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "HakedisOtomasyon.Web", "SeedData", CoolingSeedService.StoreFileName))) dir = dir.Parent;
        var seedDir = Path.Combine(dir!.FullName, "src", "HakedisOtomasyon.Web", "SeedData");

        var result = await CoolingSeedService.SeedIfNeededAsync(db, seedDir);

        Assert.Equal(0, result.StoresAdded);
        var a = await db.Stores.SingleAsync(s => s.Code == "1761");
        Assert.Equal("MMM", a.Format);
        Assert.Equal("İÇ ANADOLU", a.Region);
        Assert.Equal("ANKARA", a.City);                             // il (GENEL İCMAL sayfası için)
        Assert.Equal("BENİM ADIM", a.Name);                         // ad değişmez
        var b = await db.Stores.SingleAsync(s => s.Code == "3239");
        Assert.Equal("ÖZEL", b.Format);                             // kullanıcının girdiği korunur
        Assert.Equal("İÇ ANADOLU", b.Region);                       // boş olan doldu
    }
}
