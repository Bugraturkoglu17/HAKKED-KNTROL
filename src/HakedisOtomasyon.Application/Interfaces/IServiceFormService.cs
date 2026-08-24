using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IServiceFormService
{
    Task<List<ServiceFormDto>> GetByClaimIdAsync(int claimId);
    Task<ServiceFormDto?> GetByIdAsync(int id);
    Task<ServiceFormDto> AddFormAsync(int claimId, string filePath, string originalFileName);
    Task<ServiceFormDto> AddBlankFormAsync(int claimId);
    Task UpdateStoreAsync(int formId, string storeCode);
    Task UpdateStatusAsync(int formId, ServiceFormStatus status);
    Task SaveItemsAsync(int formId, List<ServiceItemDto> items);
    Task DeleteAsync(int id);
    Task<List<ServiceFormDto>> GetIncompleteFormsAsync(int claimId);
}
