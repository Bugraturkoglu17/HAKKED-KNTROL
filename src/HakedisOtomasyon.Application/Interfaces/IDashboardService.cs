using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(int year, int month, string? route = null);
}
