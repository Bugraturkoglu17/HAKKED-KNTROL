using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IUnitPriceListService
{
    Task<List<UnitPriceListDto>> GetAllAsync();
    Task<UnitPriceListDto?> GetActiveAsync(string companyName, string region);
    Task<UnitPriceListDto?> GetByIdAsync(int id);

    Task<List<UnitPriceItemDto>> GetItemsAsync(int unitPriceListId, bool includeInactive = false, string? search = null);
    Task<UnitPriceItemDto> CreateItemAsync(int unitPriceListId, UnitPriceItemDto dto);
    Task<UnitPriceItemDto> UpdateItemAsync(UnitPriceItemDto dto);
    Task ToggleItemActiveAsync(int itemId);
    Task<List<UnitPriceItemAuditLogDto>> GetAuditLogAsync(int itemId);

    Task<UnitPriceImportPreviewDto> ParseExcelAsync(Stream stream, string fileName);
    Task<UnitPriceListDto> ImportAsync(string companyName, string region, string name, string sourceFileName, List<UnitPriceItemDto> items);
}
