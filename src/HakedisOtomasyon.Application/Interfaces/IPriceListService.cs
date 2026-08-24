using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IPriceListService
{
    Task<List<PriceItemDto>> GetAllAsync(bool includeInactive = false, string? mainCategory = null, bool missingUnitOnly = false);
    Task<List<string>> GetMainCategoriesAsync();
    Task<List<PriceItemDto>> SearchAsync(string query);
    Task<PriceItemDto?> GetByIdAsync(int id);
    Task<PriceItemDto> CreateAsync(PriceItemDto dto);
    Task<PriceItemDto> UpdateAsync(PriceItemDto dto);
    Task ToggleActiveAsync(int id);
    Task DeleteAsync(int id);
    Task DeleteManyAsync(IEnumerable<int> ids);
    Task<PriceImportPreviewDto> ParseMigrosExcelAsync(Stream stream);
    Task<PriceImportPreviewDto> ParseGenericExcelAsync(Stream stream);
    Task<int> ImportParsedItemsAsync(List<PriceItemDto> items, bool clearExisting);
    Task<int> ImportFromExcelAsync(Stream stream);
}
