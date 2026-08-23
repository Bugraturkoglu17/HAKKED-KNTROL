using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

public class UnitPriceListService : IUnitPriceListService
{
    private readonly AppDbContext _db;
    private readonly IMaterialMatchingService _matching;

    public UnitPriceListService(AppDbContext db, IMaterialMatchingService matching)
    {
        _db = db;
        _matching = matching;
    }

    public async Task<List<UnitPriceListDto>> GetAllAsync()
    {
        var lists = await _db.UnitPriceLists.OrderByDescending(l => l.CreatedAt).ToListAsync();
        var result = new List<UnitPriceListDto>();
        foreach (var l in lists)
        {
            var count = await _db.UnitPriceItems.CountAsync(i => i.UnitPriceListId == l.Id && i.IsActive);
            var hasEur = await _db.UnitPriceItems.AnyAsync(i => i.UnitPriceListId == l.Id && i.IsActive && i.Currency == "EUR");
            result.Add(MapList(l, count, hasEur));
        }
        return result;
    }

    public async Task<UnitPriceListDto?> GetActiveAsync(string companyName, string region)
    {
        var list = await _db.UnitPriceLists
            .Where(l => l.CompanyName == companyName && l.Region == region && l.IsActive)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync();
        if (list is null) return null;
        var count = await _db.UnitPriceItems.CountAsync(i => i.UnitPriceListId == list.Id && i.IsActive);
        var hasEur = await _db.UnitPriceItems.AnyAsync(i => i.UnitPriceListId == list.Id && i.IsActive && i.Currency == "EUR");
        return MapList(list, count, hasEur);
    }

    public async Task<UnitPriceListDto?> GetByIdAsync(int id)
    {
        var list = await _db.UnitPriceLists.FindAsync(id);
        if (list is null) return null;
        var count = await _db.UnitPriceItems.CountAsync(i => i.UnitPriceListId == id && i.IsActive);
        var hasEur = await _db.UnitPriceItems.AnyAsync(i => i.UnitPriceListId == id && i.IsActive && i.Currency == "EUR");
        return MapList(list, count, hasEur);
    }

    public async Task<List<UnitPriceItemDto>> GetItemsAsync(int unitPriceListId, bool includeInactive = false, string? search = null)
    {
        var query = _db.UnitPriceItems.Where(i => i.UnitPriceListId == unitPriceListId);
        if (!includeInactive) query = query.Where(i => i.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(i => i.MaterialName.Contains(s) || (i.Spec != null && i.Spec.Contains(s)) || (i.Category != null && i.Category.Contains(s)));
        }
        var items = await query.OrderBy(i => i.Category).ThenBy(i => i.MaterialName).ToListAsync();
        return items.Select(MapItem).ToList();
    }

    public async Task<UnitPriceItemDto> CreateItemAsync(int unitPriceListId, UnitPriceItemDto dto)
    {
        var entity = new UnitPriceItem
        {
            UnitPriceListId = unitPriceListId,
            ItemCode = dto.ItemCode,
            Category = dto.Category,
            MaterialName = dto.MaterialName.Trim(),
            Brand = dto.Brand,
            Spec = dto.Spec,
            Unit = dto.Unit,
            Price = dto.Price,
            Currency = string.IsNullOrWhiteSpace(dto.Currency) ? "EUR" : dto.Currency,
            NormalizedName = _matching.Normalize($"{dto.MaterialName} {dto.Spec}"),
            IsManuallyAdded = true,
            IsActive = true,
            CreatedAt = DateTime.Now,
        };
        _db.UnitPriceItems.Add(entity);
        await _db.SaveChangesAsync();
        return MapItem(entity);
    }

    public async Task<UnitPriceItemDto> UpdateItemAsync(UnitPriceItemDto dto)
    {
        var entity = await _db.UnitPriceItems.FindAsync(dto.Id)
            ?? throw new InvalidOperationException("Birim fiyat kalemi bulunamadı.");

        LogIfChanged(entity.Id, "Malzeme Adı", entity.MaterialName, dto.MaterialName);
        LogIfChanged(entity.Id, "Tip", entity.Spec, dto.Spec);
        LogIfChanged(entity.Id, "Birim", entity.Unit, dto.Unit);
        if (entity.Price != dto.Price || entity.Currency != dto.Currency)
        {
            var oldTxt = $"{entity.Price:0.####} {entity.Currency}";
            var newTxt = $"{dto.Price:0.####} {dto.Currency}";
            _db.UnitPriceItemAuditLogs.Add(new UnitPriceItemAuditLog
            {
                UnitPriceItemId = entity.Id,
                FieldName = "Fiyat",
                OldValue = oldTxt,
                NewValue = newTxt,
                Note = $"{entity.MaterialName} fiyatı {oldTxt} → {newTxt} olarak değiştirildi.",
                ChangedAt = DateTime.Now,
            });
        }

        entity.MaterialName = dto.MaterialName.Trim();
        entity.Brand = dto.Brand;
        entity.Spec = dto.Spec;
        entity.Unit = dto.Unit;
        entity.Price = dto.Price;
        entity.Currency = string.IsNullOrWhiteSpace(dto.Currency) ? entity.Currency : dto.Currency;
        entity.Category = dto.Category;
        entity.NormalizedName = _matching.Normalize($"{entity.MaterialName} {entity.Spec}");
        entity.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync();
        return MapItem(entity);
    }

    private void LogIfChanged(int itemId, string field, string? oldVal, string? newVal)
    {
        if (oldVal == newVal) return;
        _db.UnitPriceItemAuditLogs.Add(new UnitPriceItemAuditLog
        {
            UnitPriceItemId = itemId,
            FieldName = field,
            OldValue = oldVal,
            NewValue = newVal,
            Note = $"{field}: \"{oldVal}\" → \"{newVal}\" olarak değiştirildi.",
            ChangedAt = DateTime.Now,
        });
    }

    public async Task ToggleItemActiveAsync(int itemId)
    {
        var entity = await _db.UnitPriceItems.FindAsync(itemId);
        if (entity is null) return;
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
    }

    public async Task<List<UnitPriceItemAuditLogDto>> GetAuditLogAsync(int itemId)
    {
        return await _db.UnitPriceItemAuditLogs
            .Where(a => a.UnitPriceItemId == itemId)
            .OrderByDescending(a => a.ChangedAt)
            .Select(a => new UnitPriceItemAuditLogDto
            {
                Id = a.Id,
                UnitPriceItemId = a.UnitPriceItemId,
                FieldName = a.FieldName,
                OldValue = a.OldValue,
                NewValue = a.NewValue,
                Note = a.Note,
                ChangedAt = a.ChangedAt,
            }).ToListAsync();
    }

    public Task<UnitPriceImportPreviewDto> ParseExcelAsync(Stream stream, string fileName)
        => Task.FromResult(SogutmaPriceExcelParser.Parse(stream, fileName));

    public async Task<UnitPriceListDto> ImportAsync(string companyName, string region, string name, string sourceFileName, List<UnitPriceItemDto> items)
    {
        // Aynı firma/bölge için önceki aktif listeyi pasifleştir (yeni liste aktif olur)
        var previousActive = await _db.UnitPriceLists
            .Where(l => l.CompanyName == companyName && l.Region == region && l.IsActive)
            .ToListAsync();
        foreach (var p in previousActive) p.IsActive = false;

        var list = new UnitPriceList
        {
            CompanyName = companyName,
            Region = region,
            Name = name,
            SourceFileName = sourceFileName,
            ValidFrom = DateTime.Today,
            IsActive = true,
            CreatedAt = DateTime.Now,
        };
        _db.UnitPriceLists.Add(list);
        await _db.SaveChangesAsync();

        foreach (var dto in items)
        {
            _db.UnitPriceItems.Add(new UnitPriceItem
            {
                UnitPriceListId = list.Id,
                ItemCode = dto.ItemCode,
                Category = dto.Category,
                MaterialName = dto.MaterialName,
                Brand = dto.Brand,
                Spec = dto.Spec,
                Unit = dto.Unit,
                Price = dto.Price,
                Currency = dto.Currency,
                NormalizedName = _matching.Normalize($"{dto.MaterialName} {dto.Spec}"),
                SourceFileName = dto.SourceFileName,
                SourceRowNumber = dto.SourceRowNumber,
                IsManuallyAdded = false,
                IsActive = true,
                CreatedAt = DateTime.Now,
            });
        }
        await _db.SaveChangesAsync();

        await SeedKnownAliasesAsync(list.Id, companyName);

        var count = await _db.UnitPriceItems.CountAsync(i => i.UnitPriceListId == list.Id);
        var hasEur = await _db.UnitPriceItems.AnyAsync(i => i.UnitPriceListId == list.Id && i.Currency == "EUR");
        return MapList(list, count, hasEur);
    }

    /// <summary>
    /// Kullanıcının (İNTİKOŞ SABİT FİYAT hakedişi için) doğruladığı bilinen tuzak: firma hakedişinde
    /// "1 EKİP ŞEHİR İÇİ/DIŞI SERVİS BEDELİ" yazsa da, bizim listemizdeki AYNI İSİMLİ kalem ACİL ÇAĞRI
    /// hizmetidir — firmanın kastettiği gerçek karşılık "TADİLAT ERİŞİM ŞEHİRİÇİ/ŞEHİRDIŞI" kalemidir.
    /// İsimler birebir aynı olduğu için normal motor bunu yanlışlıkla "kesin eşleşme" sayardı;
    /// bu nedenle doğru eşleşme onaylı alias olarak baştan kaydedilir.
    /// </summary>
    private async Task SeedKnownAliasesAsync(int unitPriceListId, string companyName)
    {
        await SeedAliasAsync(unitPriceListId, companyName,
            aliasText: "1 EKİP ŞEHİR İÇİ SERVİS BEDELİ",
            targetNameContains: "TADİLAT ERİŞİM", targetSpecContains: "ŞEHİRİÇİ",
            note: "Kullanıcı onayıyla önceden tanımlandı: hakedişte \"1 EKİP ŞEHİR İÇİ SERVİS BEDELİ\" yazsa da bu, katalogdaki aynı isimli (acil çağrı) kalem değil, \"Tadilat Erişim Şehiriçi\" kalemine karşılık gelir.");

        await SeedAliasAsync(unitPriceListId, companyName,
            aliasText: "1 EKİP ŞEHİR DIŞI SERVİS BEDELİ",
            targetNameContains: "TADİLAT ERİŞİM", targetSpecContains: "ŞEHİRDIŞI",
            note: "Kullanıcı onayıyla önceden tanımlandı: hakedişte \"1 EKİP ŞEHİR DIŞI SERVİS BEDELİ\" yazsa da bu, katalogdaki aynı isimli (acil çağrı) kalem değil, \"Tadilat Erişim Şehirdışı\" kalemine karşılık gelir.");
    }

    private async Task SeedAliasAsync(int unitPriceListId, string companyName, string aliasText,
        string targetNameContains, string targetSpecContains, string note)
    {
        var target = await _db.UnitPriceItems.FirstOrDefaultAsync(i =>
            i.UnitPriceListId == unitPriceListId
            && i.MaterialName.Contains(targetNameContains)
            && i.MaterialName.Contains(targetSpecContains));
        if (target is null) return;

        var normalized = _matching.Normalize(aliasText);
        var exists = await _db.MaterialAliases.AnyAsync(a => a.CompanyName == companyName && a.NormalizedAlias == normalized);
        if (exists) return;

        _db.MaterialAliases.Add(new MaterialAlias
        {
            CompanyName = companyName,
            AliasText = aliasText,
            NormalizedAlias = normalized,
            UnitPriceItemId = target.Id,
            ApprovedByUser = true,
            Note = note,
            CreatedAt = DateTime.Now,
        });
        await _db.SaveChangesAsync();
    }

    private static UnitPriceListDto MapList(UnitPriceList l, int totalItems, bool hasEur) => new()
    {
        Id = l.Id,
        CompanyName = l.CompanyName,
        Region = l.Region,
        Name = l.Name,
        SourceFileName = l.SourceFileName,
        ValidFrom = l.ValidFrom,
        ValidTo = l.ValidTo,
        IsActive = l.IsActive,
        CreatedAt = l.CreatedAt,
        UpdatedAt = l.UpdatedAt,
        TotalItems = totalItems,
        HasEurItems = hasEur,
    };

    private static UnitPriceItemDto MapItem(UnitPriceItem i) => new()
    {
        Id = i.Id,
        UnitPriceListId = i.UnitPriceListId,
        ItemCode = i.ItemCode,
        Category = i.Category,
        MaterialName = i.MaterialName,
        Brand = i.Brand,
        Spec = i.Spec,
        Unit = i.Unit,
        Price = i.Price,
        Currency = i.Currency,
        SourceFileName = i.SourceFileName,
        SourceRowNumber = i.SourceRowNumber,
        IsManuallyAdded = i.IsManuallyAdded,
        IsActive = i.IsActive,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt,
    };
}
