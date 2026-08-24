using ClosedXML.Excel;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

public class StoreService : IStoreService
{
    private readonly AppDbContext _db;
    private readonly IDataRecoveryService _recovery;
    private readonly CurrentDisciplineService _currentDiscipline;

    public StoreService(AppDbContext db, IDataRecoveryService recovery, CurrentDisciplineService currentDiscipline)
    {
        _db = db;
        _recovery = recovery;
        _currentDiscipline = currentDiscipline;
    }

    public async Task<List<StoreDto>> GetAllAsync(bool includeInactive = false)
    {
        var query = _db.Stores.Where(s => s.Discipline == _currentDiscipline.CurrentDiscipline);
        if (!includeInactive) query = query.Where(s => s.IsActive);
        return await query.OrderBy(s => s.Code)
            .Select(s => new StoreDto { Id = s.Id, Code = s.Code, Name = s.Name, IsActive = s.IsActive, Discipline = s.Discipline })
            .ToListAsync();
    }

    public async Task<StoreDto?> GetByCodeAsync(string code)
    {
        var s = await _db.Stores.FirstOrDefaultAsync(x =>
            x.Code == code.Trim().ToUpper() && x.Discipline == _currentDiscipline.CurrentDiscipline);
        return s is null ? null : new StoreDto { Id = s.Id, Code = s.Code, Name = s.Name, IsActive = s.IsActive, Discipline = s.Discipline };
    }

    public async Task<StoreDto?> GetByIdAsync(int id)
    {
        var s = await _db.Stores.FindAsync(id);
        return s is null ? null : new StoreDto { Id = s.Id, Code = s.Code, Name = s.Name, IsActive = s.IsActive, Discipline = s.Discipline };
    }

    public async Task<StoreDto> CreateAsync(StoreDto dto)
    {
        var discipline = _currentDiscipline.CurrentDiscipline;
        var code = dto.Code.Trim().ToUpper();

        // Mağaza kodu yalnızca AKTİF disiplin içinde tekil olmalı — global kontrol değil.
        var duplicate = await _db.Stores.AnyAsync(x => x.Code == code && x.Discipline == discipline);
        if (duplicate)
            throw new InvalidOperationException("Bu mağaza kodu bu modülde zaten kayıtlı.");

        var entity = new Store
        {
            Code = code,
            Name = dto.Name.Trim(),
            IsActive = dto.IsActive,
            Discipline = discipline
        };
        _db.Stores.Add(entity);
        await _db.SaveChangesAsync();
        dto.Id = entity.Id;
        dto.Discipline = discipline;
        return dto;
    }

    public async Task<StoreDto> UpdateAsync(StoreDto dto)
    {
        var entity = await _db.Stores.FindAsync(dto.Id)
            ?? throw new InvalidOperationException($"Mağaza bulunamadı: {dto.Id}");

        var code = dto.Code.Trim().ToUpper();

        // Aynı disiplinde başka bir mağaza zaten bu kodu kullanıyorsa engelle (kendisi hariç).
        var duplicate = await _db.Stores.AnyAsync(x =>
            x.Code == code && x.Discipline == entity.Discipline && x.Id != entity.Id);
        if (duplicate)
            throw new InvalidOperationException("Bu mağaza kodu bu modülde zaten kayıtlı.");

        entity.Code = code;
        entity.Name = dto.Name.Trim();
        entity.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return dto;
    }

    public async Task DeleteAsync(int id)
    {
        var entity = await _db.Stores.FindAsync(id);
        if (entity is null) return;
        entity.IsActive = false;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteManyAsync(IEnumerable<int> ids)
    {
        var idList = ids.ToList();
        var entities = await _db.Stores.Where(s => idList.Contains(s.Id)).ToListAsync();
        foreach (var e in entities) e.IsActive = false;
        await _db.SaveChangesAsync();
    }

    public async Task<int> ImportFromExcelAsync(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        int imported = 0;

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var code = row.Cell(1).GetValue<string>()?.Trim().ToUpper();
            var name = row.Cell(2).GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;

            var existing = await _db.Stores.FirstOrDefaultAsync(s =>
                s.Code == code && s.Discipline == _currentDiscipline.CurrentDiscipline);
            if (existing is null)
            {
                _db.Stores.Add(new Store { Code = code, Name = name, IsActive = true, Discipline = _currentDiscipline.CurrentDiscipline });
            }
            else
            {
                existing.Name = name;
                existing.IsActive = true;
            }
            imported++;
        }
        await _db.SaveChangesAsync();
        _ = Task.Run(() => _recovery.SaveProtectedSnapshotAsync());
        return imported;
    }
}
