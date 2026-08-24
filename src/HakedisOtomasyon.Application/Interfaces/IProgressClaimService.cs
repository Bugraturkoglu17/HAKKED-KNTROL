using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IProgressClaimService
{
    Task<List<ProgressClaimDto>> GetAllAsync();
    Task<ProgressClaimDto?> GetByIdAsync(int id);
    Task<ProgressClaimDto> CreateAsync(ProgressClaimDto dto);
    Task<ProgressClaimDto> UpdateAsync(ProgressClaimDto dto);
    Task DeleteAsync(int id);
    Task UpdateStatusAsync(int id, ProgressClaimStatus status);
    Task SaveProgressAsync(int id, int step, int? lastFormId = null);
    Task<List<ClaimSummaryDto>> GetStoreSummariesAsync(int claimId);
}
