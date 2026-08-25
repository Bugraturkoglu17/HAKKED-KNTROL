using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

public class StoreMasterService : IStoreMasterService
{
    private readonly AppDbContext _db;

    public StoreMasterService(AppDbContext db)
    {
        _db = db;
    }

    public Task<StoreImportPreviewDto> ParseExcelAsync(Stream stream, string fileName)
        => Task.FromResult(StoreExcelParser.Parse(stream, fileName));

    public async Task<int> ImportAsync(string companyName, string region, string sourceFileName, List<StoreDto> stores)
    {
        // Aynı firma/bölge için mevcut mağazaları pasifleştir; yeni liste aktif olur (UnitPriceList deseniyle tutarlı).
        var existing = await _db.Stores
            .Where(s => s.CompanyName == companyName && s.Region == region && s.IsActive)
            .ToListAsync();
        foreach (var s in existing) s.IsActive = false;

        foreach (var dto in stores)
        {
            _db.Stores.Add(new Store
            {
                CompanyName = companyName,
                Region = region,
                Code = dto.Code,
                Name = dto.Name,
                City = dto.City,
                StoreRegion = dto.StoreRegion,
                Address = dto.Address,
                NormalizedCode = TextNormalizationHelper.NormalizeCode(dto.Code),
                NormalizedName = TextNormalizationHelper.NormalizeName(dto.Name),
                IsActive = true,
                CreatedAt = DateTime.Now,
            });
        }
        await _db.SaveChangesAsync();
        return stores.Count;
    }

    public async Task<List<StoreDto>> GetAllAsync(string companyName, string region)
    {
        var stores = await _db.Stores
            .Where(s => s.CompanyName == companyName && s.Region == region && s.IsActive)
            .OrderBy(s => s.Name)
            .ToListAsync();
        return stores.Select(Map).ToList();
    }

    public Task<bool> HasAnyAsync(string companyName, string region)
        => _db.Stores.AnyAsync(s => s.CompanyName == companyName && s.Region == region && s.IsActive);

    public async Task<StoreDto?> GetByIdAsync(int id)
    {
        var s = await _db.Stores.FindAsync(id);
        return s is null ? null : Map(s);
    }

    private static StoreDto Map(Store s) => new()
    {
        Id = s.Id,
        CompanyName = s.CompanyName,
        Region = s.Region,
        Code = s.Code,
        Name = s.Name,
        City = s.City,
        StoreRegion = s.StoreRegion,
        Address = s.Address,
        IsActive = s.IsActive,
    };
}
