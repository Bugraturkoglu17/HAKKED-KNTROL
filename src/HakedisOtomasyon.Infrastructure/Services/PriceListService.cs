using ClosedXML.Excel;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace HakedisOtomasyon.Infrastructure.Services;

public class PriceListService : IPriceListService
{
    private readonly AppDbContext _db;
    private readonly IDataRecoveryService _recovery;
    private readonly CurrentDisciplineService _currentDiscipline;

    public PriceListService(AppDbContext db, IDataRecoveryService recovery, CurrentDisciplineService currentDiscipline)
    {
        _db = db;
        _recovery = recovery;
        _currentDiscipline = currentDiscipline;
    }

    // ------------------------------------------------------------------ //
    //  LISTELEME
    // ------------------------------------------------------------------ //
    public async Task<List<PriceItemDto>> GetAllAsync(
        bool includeInactive = false,
        string? mainCategory = null,
        bool missingUnitOnly = false)
    {
        var query = _db.PriceItems
            .Where(p => p.IsSelectable && p.Discipline == _currentDiscipline.CurrentDiscipline)
            .AsQueryable();
        if (!includeInactive) query = query.Where(p => p.IsActive);
        if (!string.IsNullOrEmpty(mainCategory)) query = query.Where(p => p.MainCategory == mainCategory);
        if (missingUnitOnly) query = query.Where(p => p.HasMissingUnit);

        var items = await query
            .OrderBy(p => p.MainCategory).ThenBy(p => p.SubCategory).ThenBy(p => p.Description)
            .ToListAsync();

        return items.Select(MapToDto).ToList();
    }

    public async Task<List<string>> GetMainCategoriesAsync()
    {
        return await _db.PriceItems
            .Where(p => p.IsSelectable && p.MainCategory != null && p.Discipline == _currentDiscipline.CurrentDiscipline)
            .Select(p => p.MainCategory!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    // ------------------------------------------------------------------ //
    //  AKILLI ARAMA
    // ------------------------------------------------------------------ //
    public async Task<List<PriceItemDto>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var normalized = NormalizeQuery(query);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return [];

        var candidates = await _db.PriceItems
            .Where(p => p.IsSelectable && p.Discipline == _currentDiscipline.CurrentDiscipline)   // IsActive filtresi kaldırıldı — birim eksik olsa da aranabilir
            .ToListAsync();

        // AND mantığıyla ara (tüm token'lar bulunmalı)
        var scored = candidates
            .Select(p => (item: p, score: ScoreItem(p, tokens, andMode: true)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(20)
            .ToList();

        // AND sıfır sonuç verirse OR mantığına düş
        if (!scored.Any() && tokens.Length > 1)
        {
            scored = candidates
                .Select(p => (item: p, score: ScoreItem(p, tokens, andMode: false)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(20)
                .ToList();
        }

        return scored.Select(x => MapToDto(x.item)).ToList();
    }

    private static int ScoreItem(PriceItem p, string[] tokens, bool andMode)
    {
        var searchText = NormalizeText(p.SearchText ?? BuildSearchTextFromItem(p));
        int score = 0;
        foreach (var token in tokens)
        {
            if (searchText.Contains(token))
                score += 10;
            else if (andMode)
                return 0; // AND: hepsi bulunmali
        }
        if (score > 0 && NormalizeText(p.Description).Contains(string.Join(" ", tokens)))
            score += 5;
        return score;
    }

    // ------------------------------------------------------------------ //
    //  CRUD
    // ------------------------------------------------------------------ //
    public async Task<PriceItemDto?> GetByIdAsync(int id)
    {
        var p = await _db.PriceItems.FindAsync(id);
        return p is null ? null : MapToDto(p);
    }

    public async Task<PriceItemDto> CreateAsync(PriceItemDto dto)
    {
        var entity = new PriceItem
        {
            MainCategory = dto.MainCategory?.Trim(),
            SubCategory = dto.SubCategory?.Trim(),
            SubCategory2 = dto.SubCategory2?.Trim(),
            Description = dto.Description.Trim(),
            Unit = dto.Unit.Trim(),
            MaterialPrice = dto.MaterialPrice,
            LaborPrice = dto.LaborPrice,
            PriceType = dto.PriceType,
            IsSelectable = true,
            IsActive = dto.IsActive,
            IsManuallyAdded = true,
            HasMissingUnit = string.IsNullOrEmpty(dto.Unit),
            Discipline = _currentDiscipline.CurrentDiscipline,
            Notes = dto.Notes,
        };
        entity.DisplayName = entity.SubCategory2 != null ? $"{entity.SubCategory2} > {entity.Description}"
            : entity.SubCategory != null ? $"{entity.SubCategory} > {entity.Description}"
            : entity.Description;
        entity.InvoiceDescription = entity.SubCategory2 != null ? $"{entity.SubCategory2} - {entity.Description}"
            : entity.SubCategory != null ? $"{entity.SubCategory} - {entity.Description}"
            : entity.Description;
        entity.SearchText = BuildSearchTextFromItem(entity);

        _db.PriceItems.Add(entity);
        await _db.SaveChangesAsync();
        dto.Id = entity.Id;
        return MapToDto(entity);
    }

    public async Task<PriceItemDto> UpdateAsync(PriceItemDto dto)
    {
        var entity = await _db.PriceItems.FindAsync(dto.Id)
            ?? throw new InvalidOperationException($"Fiyat kalemi bulunamadi: {dto.Id}");
        entity.MainCategory = dto.MainCategory?.Trim();
        entity.SubCategory = dto.SubCategory?.Trim();
        entity.SubCategory2 = dto.SubCategory2?.Trim();
        entity.Description = dto.Description.Trim();
        entity.Unit = dto.Unit.Trim();
        entity.MaterialPrice = dto.MaterialPrice;
        entity.LaborPrice = dto.LaborPrice;
        entity.PriceType = dto.PriceType;
        entity.IsActive = dto.IsActive;
        entity.HasMissingUnit = string.IsNullOrEmpty(dto.Unit);
        entity.Notes = dto.Notes;
        entity.UpdatedAt = DateTime.Now;
        entity.DisplayName = entity.SubCategory2 != null ? $"{entity.SubCategory2} > {entity.Description}"
            : entity.SubCategory != null ? $"{entity.SubCategory} > {entity.Description}"
            : entity.Description;
        entity.InvoiceDescription = entity.SubCategory2 != null ? $"{entity.SubCategory2} - {entity.Description}"
            : entity.SubCategory != null ? $"{entity.SubCategory} - {entity.Description}"
            : entity.Description;
        entity.SearchText = BuildSearchTextFromItem(entity);
        await _db.SaveChangesAsync();
        return MapToDto(entity);
    }

    public async Task ToggleActiveAsync(int id)
    {
        var entity = await _db.PriceItems.FindAsync(id);
        if (entity is null) return;
        entity.IsActive = !entity.IsActive;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var entity = await _db.PriceItems.FindAsync(id);
        if (entity is null) return;
        _db.PriceItems.Remove(entity);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteManyAsync(IEnumerable<int> ids)
    {
        var idList = ids.ToList();
        var entities = await _db.PriceItems.Where(p => idList.Contains(p.Id)).ToListAsync();
        _db.PriceItems.RemoveRange(entities);
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ //
    //  MIGROS EXCEL IMPORT
    // ------------------------------------------------------------------ //
    public Task<PriceImportPreviewDto> ParseMigrosExcelAsync(Stream stream)
    {
        var preview = MigrosPriceExcelParser.Parse(stream);
        return Task.FromResult(preview);
    }

    public Task<PriceImportPreviewDto> ParseGenericExcelAsync(Stream stream)
    {
        var preview = GenericPriceExcelParser.Parse(stream);
        return Task.FromResult(preview);
    }

    public async Task<int> ImportParsedItemsAsync(List<PriceItemDto> items, bool clearExisting)
    {
        var discipline = _currentDiscipline.CurrentDiscipline;

        if (clearExisting)
        {
            var toRemove = await _db.PriceItems
                .Where(p => !p.IsManuallyAdded && p.Discipline == discipline)
                .ToListAsync();
            _db.PriceItems.RemoveRange(toRemove);
            await _db.SaveChangesAsync();
        }

        // Upsert anahtari: MainCategory + Description + Unit (kucuk harf, trim)
        // Sadece AKTİF disiplinin kendi kalemleri arasında eşleştirme yapılır —
        // Klima importu Yangın kalemlerinin üzerine asla yazmaz.
        Dictionary<string, PriceItem> existingMap;
        if (clearExisting)
        {
            existingMap = new Dictionary<string, PriceItem>();
        }
        else
        {
            var existingList = await _db.PriceItems
                .Where(p => !p.IsManuallyAdded && p.Discipline == discipline)
                .ToListAsync();
            existingMap = existingList
                .GroupBy(p => UpsertKey(p.MainCategory, p.Description, p.Unit))
                .ToDictionary(g => g.Key, g => g.First());
        }

        int imported = 0;
        foreach (var dto in items.Where(i => i.IsSelectable))
        {
            var key = UpsertKey(dto.MainCategory, dto.Description, dto.Unit);

            if (existingMap.TryGetValue(key, out var existing))
            {
                // Mevcut kaydi guncelle
                existing.InvoiceDescription = dto.InvoiceDescription;
                existing.DisplayName = dto.DisplayName ?? dto.Description;
                existing.MaterialPrice = dto.MaterialPrice;
                existing.LaborPrice = dto.LaborPrice;
                existing.PriceType = dto.PriceType;
                existing.IsActive = !dto.HasMissingUnit;
                existing.HasMissingUnit = dto.HasMissingUnit;
                existing.PozNo = dto.PozNo;
                existing.SourceSheetName = dto.SourceSheetName;
                existing.SourceRowNumber = dto.SourceRowNumber;
                existing.UpdatedAt = DateTime.Now;
                existing.IsCurrencyBased = dto.IsCurrencyBased;
                existing.CurrencyCode = dto.CurrencyCode;
                existing.ListPriceUsd = dto.ListPriceUsd;
                existing.DiscountRate = dto.DiscountRate;
                existing.DiscountedUsdPrice = dto.DiscountedUsdPrice;
                existing.ExchangeRateRequired = dto.ExchangeRateRequired;
                existing.MainCategory = dto.MainCategory;
                existing.SubCategory = dto.SubCategory;
                existing.SubCategory2 = dto.SubCategory2;
                existing.Notes = dto.Notes;
                existing.SearchText = BuildSearchTextFromItem(existing);
            }
            else
            {
                var entity = new PriceItem
                {
                    SourceSheetName = dto.SourceSheetName,
                    SourceRowNumber = dto.SourceRowNumber,
                    PozNo = dto.PozNo,
                    MainCategory = dto.MainCategory,
                    SubCategory = dto.SubCategory,
                    SubCategory2 = dto.SubCategory2,
                    Notes = dto.Notes,
                    Description = dto.Description,
                    DisplayName = dto.DisplayName ?? dto.Description,
                    InvoiceDescription = dto.InvoiceDescription,
                    Unit = dto.Unit,
                    MaterialPrice = dto.MaterialPrice,
                    LaborPrice = dto.LaborPrice,
                    PriceType = dto.PriceType,
                    IsSelectable = true,
                    IsActive = !dto.HasMissingUnit,
                    IsManuallyAdded = false,
                    HasMissingUnit = dto.HasMissingUnit,
                    IsCurrencyBased = dto.IsCurrencyBased,
                    CurrencyCode = dto.CurrencyCode,
                    ListPriceUsd = dto.ListPriceUsd,
                    DiscountRate = dto.DiscountRate,
                    DiscountedUsdPrice = dto.DiscountedUsdPrice,
                    ExchangeRateRequired = dto.ExchangeRateRequired,
                    Discipline = discipline,
                };
                entity.SearchText = BuildSearchTextFromItem(entity);
                _db.PriceItems.Add(entity);
                existingMap[key] = entity; // ayni import icinde duplicate onle
            }
            imported++;
        }
        await _db.SaveChangesAsync();
        _ = Task.Run(() => _recovery.SaveProtectedSnapshotAsync());
        return imported;
    }

    // ------------------------------------------------------------------ //
    //  ESKI FORMAT (geriye donuk uyumluluk)
    // ------------------------------------------------------------------ //
    public async Task<int> ImportFromExcelAsync(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        int imported = 0;

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var description = row.Cell(1).GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(description)) continue;

            var unit = row.Cell(2).GetValue<string>()?.Trim() ?? string.Empty;
            var material = row.Cell(3).TryGetValue<decimal>(out var m) ? m : 0;
            var labor = row.Cell(4).TryGetValue<decimal>(out var l) ? l : 0;

            var existing = await _db.PriceItems.FirstOrDefaultAsync(p =>
                p.Description == description && p.IsManuallyAdded && p.Discipline == _currentDiscipline.CurrentDiscipline);
            if (existing is null)
            {
                var e = new PriceItem
                {
                    Description = description, Unit = unit,
                    MaterialPrice = material, LaborPrice = labor,
                    IsActive = true, IsSelectable = true, IsManuallyAdded = true,
                    Discipline = _currentDiscipline.CurrentDiscipline,
                    PriceType = (material > 0 && labor > 0) ? PriceType.FixedPrice
                        : labor > 0 ? PriceType.LaborOnly
                        : PriceType.MaterialOnly
                };
                e.DisplayName = e.Description;
                e.InvoiceDescription = e.Description;
                e.SearchText = BuildSearchTextFromItem(e);
                _db.PriceItems.Add(e);
            }
            else
            {
                existing.Unit = unit;
                existing.MaterialPrice = material;
                existing.LaborPrice = labor;
                existing.IsActive = true;
            }
            imported++;
        }
        await _db.SaveChangesAsync();
        _ = Task.Run(() => _recovery.SaveProtectedSnapshotAsync());
        return imported;
    }

    // ------------------------------------------------------------------ //
    //  YARDIMCI
    // ------------------------------------------------------------------ //
    private static PriceItemDto MapToDto(PriceItem p) => new()
    {
        Id = p.Id,
        SourceSheetName = p.SourceSheetName,
        SourceRowNumber = p.SourceRowNumber,
        PozNo = p.PozNo,
        MainCategory = p.MainCategory,
        SubCategory = p.SubCategory,
        SubCategory2 = p.SubCategory2,
        Description = p.Description,
        DisplayName = p.DisplayName,
        InvoiceDescription = p.InvoiceDescription,
        Unit = p.Unit,
        MaterialPrice = p.MaterialPrice,
        LaborPrice = p.LaborPrice,
        PriceType = p.PriceType,
        IsSelectable = p.IsSelectable,
        IsActive = p.IsActive,
        IsManuallyAdded = p.IsManuallyAdded,
        HasMissingUnit = p.HasMissingUnit,
        IsCurrencyBased = p.IsCurrencyBased,
        CurrencyCode = p.CurrencyCode,
        ListPriceUsd = p.ListPriceUsd,
        DiscountRate = p.DiscountRate,
        DiscountedUsdPrice = p.DiscountedUsdPrice,
        ExchangeRateRequired = p.ExchangeRateRequired,
        Discipline = p.Discipline,
        Notes = p.Notes,
    };

    private static string UpsertKey(string? mainCat, string? desc, string? unit)
        => $"{mainCat?.Trim().ToLowerInvariant()}|{desc?.Trim().ToLowerInvariant()}|{unit?.Trim().ToLowerInvariant()}";

    private static string BuildSearchTextFromItem(PriceItem p)
    {
        var parts = new List<string?> { p.MainCategory, p.Description, p.InvoiceDescription, p.Unit, p.PozNo };
        return string.Join(" ", parts.Where(x => !string.IsNullOrEmpty(x)));
    }

    // ---- Arama normalizasyonu ----
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spring"] = "sprinkler",
        ["sprink"] = "sprinkler",
        ["sarkik"] = "pendent sprinkler",
        ["flex"] = "flexible",
        ["fleks"] = "flexible",
        ["kanal"] = "hava kanali",
        ["menfez"] = "difuzor",
        ["diffuser"] = "difuzor",
        ["izoleli"] = "izolasyonlu",
        ["izolesiz"] = "izolasyonsuz",
    };

    private static string NormalizeQuery(string query)
    {
        // Normalize each word separately to preserve multi-word search.
        // e.g. "Ø224 spiro" → ["cap224", "spiro"] instead of merged "cap224spiro"
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(" ", words.Select(NormalizeText).Where(w => !string.IsNullOrEmpty(w)));
        foreach (var (alias, replacement) in _aliases)
        {
            var normalizedAlias = NormalizeText(alias);
            if (text.Contains(normalizedAlias))
                text = text.Replace(normalizedAlias, NormalizeText(replacement));
        }
        return text;
    }

    internal static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.ToLowerInvariant()
            .Replace("\u00f8", "cap")  // Ø (diameter) = cap (= çap normalized)
            .Replace('\u00e7', 'c').Replace('\u011f', 'g').Replace('\u0131', 'i')
            .Replace('\u00f6', 'o').Replace('\u015f', 's').Replace('\u00fc', 'u')
            .Replace("m\u00b3", "m3").Replace("m\u00b2", "m2");
        // Harf-rakam ve rakam-harf arası boşlukları sil: "DN 100" → "dn100"
        text = Regex.Replace(text, @"(?<=[a-z])\s+(?=\d)", "");
        text = Regex.Replace(text, @"(?<=\d)\s+(?=[a-z])", "");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
