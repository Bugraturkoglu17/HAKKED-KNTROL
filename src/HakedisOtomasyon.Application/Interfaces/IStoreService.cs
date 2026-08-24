using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IStoreService
{
    Task<List<StoreDto>> GetAllAsync(bool includeInactive = false);
    Task<StoreDto?> GetByCodeAsync(string code);
    Task<StoreDto?> GetByIdAsync(int id);
    Task<StoreDto> CreateAsync(StoreDto dto);
    Task<StoreDto> UpdateAsync(StoreDto dto);
    Task DeleteAsync(int id);
    Task DeleteManyAsync(IEnumerable<int> ids);
    Task<int> ImportFromExcelAsync(Stream stream);
}
