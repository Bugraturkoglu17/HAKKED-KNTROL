using System.Text.Json;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Soğutma disiplini için kurulumla birlikte gelen başlangıç verisini (birim fiyat listesi + mağaza
/// listesi) içeri alır. Veri, uygulama klasöründeki SeedData\cooling_price_items.json ve
/// SeedData\cooling_stores.json dosyalarındadır.
///
/// Kurallar (kullanıcının mevcut verisini asla bozmaz):
///  • Mağazalar: kurulum başına YALNIZCA BİR KEZ (CoolingSeedV1); kod bazında eklenir, zaten kayıtlı
///    kod atlanır (adı/durumu değiştirilmez). Kullanıcı sonradan silerse yeniden eklenmez.
///  • Fiyat listesi: yayımlanan liste güncellendikçe sürüm bayrağı artar (şu an CoolingPricesV2).
///      – Soğutma'da hiç kalem yoksa tüm liste eklenir.
///      – Önceden seed'den gelmiş (SourceSheetName etiketli, elle eklenmemiş) kalemler varsa listedeki
///        güncel değerlerle YENİLENİR: fiyat/para birimi/ad/ana grup güncellenir, yeni kalemler eklenir,
///        listeden kalkan ve kullanıcının dokunmadığı kalemler silinir. Kullanıcının girdiği birim,
///        aktif/pasif durumu ve kendi eklediği kalemler KORUNUR.
///      – Soğutma'da yalnızca kullanıcının kendi kalemleri varsa fiyat listesine dokunulmaz.
///  • Para birimi korunur: EUR kalemler ForeignPrice + CurrencyCode="EUR" olarak saklanır (TL alanları 0),
///    TL kalemler MaterialPrice'a yazılır (Soğutma'da tek "Birim Fiyat" — işçilik/malzeme ayrımı yoktur).
///  • Yalnızca Soğutma disiplinine yazar; Yangın/Klima/Asansör verisine dokunmaz.
/// </summary>
public static class CoolingSeedService
{
    public const string SeedFlagKey = "CoolingSeedV1";          // mağazalar + ilk fiyat yüklemesi
    public const string PriceRefreshFlagKey = "CoolingPricesV2"; // 2026 Yayım listesi (Marka sütunsuz, tek Birim Fiyat)
    public const string PriceFileName = "cooling_price_items.json";
    public const string StoreFileName = "cooling_stores.json";
    public const string SourceSheetLabel = "2026 Soğutma Malzeme Birim Fiyatlar (Yayım)";

    public record SeedResult(bool Applied, int PriceItemsAdded, int StoresAdded, string? SkippedReason,
        int PriceItemsUpdated = 0, int PriceItemsRemoved = 0);

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

    private static string Key(string? description)
        => string.Join(' ', (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static async Task<SeedResult> SeedIfNeededAsync(AppDbContext db, string seedDirectory)
    {
        var storesDone = await db.AppSettings.AnyAsync(s => s.Key == SeedFlagKey);
        var pricesDone = await db.AppSettings.AnyAsync(s => s.Key == PriceRefreshFlagKey);
        if (storesDone && pricesDone)
            return new SeedResult(false, 0, 0, "Bu kurulumda daha önce uygulandı.");

        var pricePath = Path.Combine(seedDirectory, PriceFileName);
        var storePath = Path.Combine(seedDirectory, StoreFileName);
        if (!File.Exists(pricePath) && !File.Exists(storePath))
            return new SeedResult(false, 0, 0, "Seed dosyaları bulunamadı."); // bayrak konmaz — dosyalar gelince tekrar denenir

        int added = 0, updated = 0, removed = 0, storesAdded = 0;

        // ── Fiyat listesi ───────────────────────────────────────────────────
        if (!pricesDone && File.Exists(pricePath))
        {
            var prices = (JsonSerializer.Deserialize<List<SeedPrice>>(await File.ReadAllTextAsync(pricePath), Json) ?? new())
                .Where(p => !string.IsNullOrWhiteSpace(p.Description)).ToList();
            var cooling = await db.PriceItems.Where(p => p.Discipline == MechanicalDiscipline.Cooling).ToListAsync();

            if (cooling.Count == 0)
            {
                foreach (var sp in prices) { db.PriceItems.Add(NewItem(sp)); added++; }
            }
            else
            {
                var seeded = cooling.Where(p => !p.IsManuallyAdded && p.SourceSheetName == SourceSheetLabel).ToList();
                if (seeded.Count > 0)
                {
                    var existing = new Dictionary<string, PriceItem>();
                    foreach (var p in seeded) existing.TryAdd(Key(p.Description), p);
                    var keep = new HashSet<PriceItem>();

                    foreach (var sp in prices)
                    {
                        if (existing.TryGetValue(Key(sp.Description), out var item))
                        {
                            ApplyPrice(item, sp);   // birim / aktif-pasif kullanıcıda kalır; UpdatedAt'e dokunulmaz (kullanıcı düzenlemesi sayılmaz)
                            keep.Add(item);
                            updated++;
                        }
                        else { db.PriceItems.Add(NewItem(sp)); added++; }
                    }
                    // Listeden kalkan ve kullanıcının hiç düzenlemediği seed kalemleri silinir (kopya/eski ad kalmasın).
                    foreach (var p in seeded.Where(p => !keep.Contains(p) && p.UpdatedAt == null))
                    {
                        db.PriceItems.Remove(p);
                        removed++;
                    }
                }
                // else: Soğutma'da yalnızca kullanıcının kendi kalemleri var — dokunma.
            }
        }

        // ── Mağazalar (kurulum başına bir kez) ──────────────────────────────
        if (!storesDone && File.Exists(storePath))
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

        var now = DateTime.Now.ToString("O");
        if (!storesDone)
            db.AppSettings.Add(new AppSetting { Key = SeedFlagKey, Value = now, Description = "Soğutma mağaza listesi + ilk fiyat listesi kurulumdan içeri alındı." });
        if (!pricesDone)
            db.AppSettings.Add(new AppSetting { Key = PriceRefreshFlagKey, Value = now, Description = "Soğutma birim fiyat listesi 2026 Yayım sürümüyle güncellendi." });
        await db.SaveChangesAsync();
        return new SeedResult(true, added, storesAdded, null, updated, removed);
    }

    private static PriceItem NewItem(SeedPrice sp)
    {
        var entity = new PriceItem
        {
            SourceSheetName = SourceSheetLabel,
            // Kaynak listede birim sütunu yok — kullanıcı "Birimi Eksik" filtresiyle tamamlayabilir.
            // Kalemler yine de listede görünsün diye aktif bırakılır.
            Unit = string.Empty,
            HasMissingUnit = true,
            IsActive = true,
            IsSelectable = true,
            IsManuallyAdded = false,
            Discipline = MechanicalDiscipline.Cooling,
        };
        ApplyPrice(entity, sp);
        return entity;
    }

    /// <summary>Listeden gelen değerleri uygular (birim, aktiflik ve elle-eklenmişlik dokunulmaz).</summary>
    private static void ApplyPrice(PriceItem entity, SeedPrice sp)
    {
        var isEur = string.Equals(sp.Currency, "EUR", StringComparison.OrdinalIgnoreCase);
        var description = sp.Description.Length > 500 ? sp.Description[..500] : sp.Description;

        entity.PozNo = sp.PozNo;
        entity.MainCategory = sp.MainCategory;
        entity.Description = description;
        entity.DisplayName = description;
        entity.Notes = sp.Notes;
        entity.PriceType = sp.Price == 0m ? PriceType.VariablePrice : PriceType.FixedPrice;
        entity.LaborPrice = 0m; // Soğutma'da işçilik ayrımı yok — tek "Birim Fiyat"

        if (isEur)
        {
            entity.IsCurrencyBased = true;
            entity.CurrencyCode = "EUR";
            entity.ForeignPrice = sp.Price;
            entity.ExchangeRateRequired = true;
            entity.MaterialPrice = 0m;
        }
        else
        {
            entity.IsCurrencyBased = false;
            entity.CurrencyCode = null;
            entity.ForeignPrice = null;
            entity.ExchangeRateRequired = false;
            entity.MaterialPrice = sp.Price;   // TL birim fiyat (alan adı eski şemadan "MaterialPrice")
        }

        entity.SearchText = PriceListService.BuildSearchTextFromItem(entity);
    }
}
