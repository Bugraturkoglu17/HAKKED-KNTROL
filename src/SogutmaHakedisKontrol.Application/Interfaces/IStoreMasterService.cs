using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IStoreMasterService
{
    Task<StoreImportPreviewDto> ParseExcelAsync(Stream stream, string fileName);
    Task<int> ImportAsync(string companyName, string region, string sourceFileName, List<StoreDto> stores);
    Task<List<StoreDto>> GetAllAsync(string companyName, string region);
    Task<bool> HasAnyAsync(string companyName, string region);
    Task<StoreDto?> GetByIdAsync(int id);
}
