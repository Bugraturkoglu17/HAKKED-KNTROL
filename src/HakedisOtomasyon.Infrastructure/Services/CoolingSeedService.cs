using System.Text.Json;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Soğutma disiplini için kurulumla birlikte gelen başlangıç verisini (birim fiyat listesi + mağaza
/// listesi) ilk açılışta içeri alır. Veri, uygulama klasöründeki SeedData\cooling_price_items.json ve
/// SeedData\cooling_stores.json dosyalarındadır.
///
/// Kurallar (kullanıcının mevcut verisini asla bozmaz):
///  • Her kurulum için YALNIZCA BİR KEZ çalışır (AppSetting bayrağı) — kullanıcı sonradan kalemleri
///    silerse yeniden eklenmez.
///  • Soğutma fiyat listesinde zaten kalem varsa fiyatlar eklenmez (kullanıcının kendi listesi korunur).
///  • Mağazalar kod bazında eklenir; zaten kayıtlı bir kod atlanır (adı/durumu değiştirilmez).
///  • Para birimi korunur: EUR kalemler ForeignPrice + CurrencyCode="EUR" olarak saklanır (TL alanları 0),
///    TL kalemler MaterialPrice'a yazılır. Hiçbir EUR tutar TL gibi saklanmaz.
/// </summary>
public static class CoolingSeedService
{
    public const string SeedFlagKey = "CoolingSeedV1";
    public const string PriceFileName = "cooling_price_items.json";
    public const string StoreFileName = "cooling_stores.json";
    private const string SourceSheetLabel = "2026 Soğutma Malzeme Birim Fiyatlar (Yayım)";

    public record SeedResult(bool Applied, int PriceItemsAdded, int StoresAdded, string? SkippedReason);

    private class SeedPrice
    {
        public string? PozNo { get; set; }
        public string? MainCategory { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public string Currency { get; set; } = "TRY";
        public decimal Price { get; set; }
    }

    private class SeedStore
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Active { get; set; } = true;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<SeedResult> SeedIfNeededAsync(AppDbContext db, string seedDirectory)
    {
        if (await db.AppSettings.AnyAsync(s => s.Key == SeedFlagKey))
            return new SeedResult(false, 0, 0, "Bu kurulumda daha önce uygulandı.");

        var pricePath = Path.Combine(seedDirectory, PriceFileName);
        var storePath = Path.Combine(seedDirectory, StoreFileName);
        if (!File.Exists(pricePath) && !File.Exists(storePath))
            return new SeedResult(false, 0, 0, "Seed dosyaları bulunamadı."); // bayrak konmaz — dosyalar gelince tekrar denenir

        int pricesAdded = 0, storesAdded = 0;

        if (File.Exists(pricePath) && !await db.PriceItems.AnyAsync(p => p.Discipline == MechanicalDiscipline.Cooling))
        {
            var prices = JsonSerializer.Deserialize<List<SeedPrice>>(await File.ReadAllTextAsync(pricePath), Json) ?? new();
            foreach (var sp in prices.Where(p => !string.IsNullOrWhiteSpace(p.Description)))
            {
                var entity = MapPrice(sp);
                db.PriceItems.Add(entity);
                pricesAdded++;
            }
        }

        if (File.Exists(storePath))
        {
            var stores = JsonSerializer.Deserialize<List<SeedStore>>(await File.ReadAllTextAsync(storePath), Json) ?? new();
            var existingCodes = (await db.Stores
                    .Where(s => s.Discipline == MechanicalDiscipline.Cooling)
                    .Select(s => s.Code)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var ss in stores)
            {
                var code = ss.Code?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(ss.Name)) continue;
                if (!existingCodes.Add(code)) continue; // aynı kod bir kez
                db.Stores.Add(new Store
                {
                    Code = code,
                    Name = ss.Name.Trim(),
                    IsActive = ss.Active,
                    Discipline = MechanicalDiscipline.Cooling,
                });
                storesAdded++;
            }
        }

        db.AppSettings.Add(new AppSetting
        {
            Key = SeedFlagKey,
            Value = DateTime.Now.ToString("O"),
            Description = "Soğutma başlangıç verisi (fiyat listesi + mağazalar) kurulumdan içeri alındı.",
        });
        await db.SaveChangesAsync();
        return new SeedResult(true, pricesAdded, storesAdded, null);
    }

    private static PriceItem MapPrice(SeedPrice sp)
    {
        var isEur = string.Equals(sp.Currency, "EUR", StringComparison.OrdinalIgnoreCase);
        var description = sp.Description.Length > 500 ? sp.Description[..500] : sp.Description;
        var entity = new PriceItem
        {
            SourceSheetName = SourceSheetLabel,
            PozNo = sp.PozNo,
            MainCategory = sp.MainCategory,
            Description = description,
            DisplayName = description,
            Notes = sp.Notes,
            // Kaynak listede birim sütunu yok — kullanıcı "Birimi Eksik" filtresiyle tamamlayabilir.
            // Kalemler yine de listede görünsün diye aktif bırakılır.
            Unit = string.Empty,
            HasMissingUnit = true,
            IsActive = true,
            IsSelectable = true,
            IsManuallyAdded = false,
            PriceType = sp.Price == 0m ? PriceType.VariablePrice : PriceType.FixedPrice,
            Discipline = MechanicalDiscipline.Cooling,
        };

        if (isEur)
        {
            entity.IsCurrencyBased = true;
            entity.CurrencyCode = "EUR";
            entity.ForeignPrice = sp.Price;
            entity.ExchangeRateRequired = true;
        }
        else
        {
            entity.MaterialPrice = sp.Price;
        }

        entity.SearchText = PriceListService.BuildSearchTextFromItem(entity);
        return entity;
    }
}
