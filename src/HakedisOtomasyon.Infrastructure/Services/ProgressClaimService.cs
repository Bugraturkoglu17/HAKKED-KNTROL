using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

public class ProgressClaimService : IProgressClaimService
{
    private readonly AppDbContext _db;
    private readonly CurrentDisciplineService _currentDiscipline;

    public ProgressClaimService(AppDbContext db, CurrentDisciplineService currentDiscipline)
    {
        _db = db;
        _currentDiscipline = currentDiscipline;
    }

    public async Task<List<ProgressClaimDto>> GetAllAsync()
    {
        var claims = await _db.ProgressClaims
            .Where(c => c.Discipline == _currentDiscipline.CurrentDiscipline)
            .Include(c => c.ServiceForms)
                .ThenInclude(f => f.ServiceItems)
            .Include(c => c.ServiceForms)
                .ThenInclude(f => f.Invoices)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        return claims.Select(c => new ProgressClaimDto
        {
            Id = c.Id, Name = c.Name, CompanyName = c.CompanyName,
            Month = c.Month, Year = c.Year, Description = c.Description,
            CreatedAt = c.CreatedAt, Status = c.Status,
            CurrentStep = c.CurrentStep, LastOpenedServiceFormId = c.LastOpenedServiceFormId,
            ClaimType = c.ClaimType, Discipline = c.Discipline,
            FormCount = c.ServiceForms.Count,
            CompletedFormCount = c.ServiceForms.Count(f => f.Status == ServiceFormStatus.Tamamlandi || f.Status == ServiceFormStatus.DisaAktarildi),
            TotalAmount = c.ServiceForms.SelectMany(f => f.ServiceItems).Sum(i => i.TotalPrice)
                        + c.ServiceForms.SelectMany(f => f.Invoices).Sum(i => i.CalculatedTotal)
        }).ToList();
    }

    public async Task<ProgressClaimDto?> GetByIdAsync(int id)
    {
        var c = await _db.ProgressClaims
            .Include(c => c.ServiceForms).ThenInclude(f => f.ServiceItems)
            .Include(c => c.ServiceForms).ThenInclude(f => f.Store)
            .Include(c => c.ServiceForms).ThenInclude(f => f.Invoices)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (c is null) return null;
        return new ProgressClaimDto
        {
            Id = c.Id, Name = c.Name, CompanyName = c.CompanyName,
            Month = c.Month, Year = c.Year, Description = c.Description,
            CreatedAt = c.CreatedAt, Status = c.Status,
            CurrentStep = c.CurrentStep, LastOpenedServiceFormId = c.LastOpenedServiceFormId,
            ClaimType = c.ClaimType, Discipline = c.Discipline,
            FormCount = c.ServiceForms.Count,
            CompletedFormCount = c.ServiceForms.Count(f => f.Status == ServiceFormStatus.Tamamlandi || f.Status == ServiceFormStatus.DisaAktarildi),
            TotalAmount = c.ServiceForms.SelectMany(f => f.ServiceItems).Sum(i => i.TotalPrice)
                        + c.ServiceForms.SelectMany(f => f.Invoices).Sum(i => i.CalculatedTotal)
        };
    }

    public async Task<ProgressClaimDto> CreateAsync(ProgressClaimDto dto)
    {
        var entity = new ProgressClaim
        {
            Name = dto.Name.Trim(),
            CompanyName = dto.CompanyName.Trim(),
            Month = dto.Month,
            Year = dto.Year,
            Description = dto.Description,
            Status = ProgressClaimStatus.Taslak,
            ClaimType = dto.ClaimType,
            Discipline = _currentDiscipline.CurrentDiscipline
        };
        _db.ProgressClaims.Add(entity);
        await _db.SaveChangesAsync();
        dto.Id = entity.Id;
        dto.CreatedAt = entity.CreatedAt;
        dto.Status = entity.Status;
        return dto;
    }

    public async Task<ProgressClaimDto> UpdateAsync(ProgressClaimDto dto)
    {
        var entity = await _db.ProgressClaims.FindAsync(dto.Id)
            ?? throw new InvalidOperationException($"Hakediş bulunamadı: {dto.Id}");
        entity.Name = dto.Name.Trim();
        entity.CompanyName = dto.CompanyName.Trim();
        entity.Month = dto.Month;
        entity.Year = dto.Year;
        entity.Description = dto.Description;
        entity.ClaimType = dto.ClaimType;
        await _db.SaveChangesAsync();
        return dto;
    }

    public async Task DeleteAsync(int id)
    {
        var entity = await _db.ProgressClaims.FindAsync(id);
        if (entity is null) return;
        _db.ProgressClaims.Remove(entity);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateStatusAsync(int id, ProgressClaimStatus status)
    {
        var entity = await _db.ProgressClaims.FindAsync(id);
        if (entity is null) return;
        entity.Status = status;
        await _db.SaveChangesAsync();
    }

    public async Task SaveProgressAsync(int id, int step, int? lastFormId = null)
    {
        var entity = await _db.ProgressClaims.FindAsync(id);
        if (entity is null) return;
        entity.CurrentStep = step;
        if (lastFormId.HasValue) entity.LastOpenedServiceFormId = lastFormId;
        if (step >= 1 && entity.Status == ProgressClaimStatus.Taslak)
            entity.Status = ProgressClaimStatus.Isleniyor;
        await _db.SaveChangesAsync();
    }

    public async Task<List<ClaimSummaryDto>> GetStoreSummariesAsync(int claimId)
    {
        var forms = await _db.ServiceForms
            .Include(f => f.Store)
            .Include(f => f.ServiceItems)
            .Include(f => f.Invoices.Where(i => !i.IsArchived))
            .Where(f => f.ProgressClaimId == claimId && f.StoreId != null)
            .ToListAsync();

        return forms
            .GroupBy(f => new { f.StoreId, f.Store!.Code, f.Store!.Name })
            .Select(g => new ClaimSummaryDto
            {
                ClaimId = claimId,
                StoreCode = g.Key.Code,
                StoreName = g.Key.Name,
                ServiceFeeTotal = g.SelectMany(f => f.ServiceItems)
                    .Where(i => i.Description == "Servis Ücreti" && !i.IsInvoiceBased)
                    .Sum(i => i.TotalPrice),
                PriceListTotal = g.SelectMany(f => f.ServiceItems)
                    .Where(i => !i.IsInvoiceBased && i.Description != "Servis Ücreti")
                    .Sum(i => i.TotalPrice),
                InvoiceTotal = g.SelectMany(f => f.ServiceItems)
                    .Where(i => i.IsInvoiceBased)
                    .Sum(i => i.TotalPrice),
                FormCount = g.Count(),
                InvoiceCount = g.Sum(f => f.Invoices.Count)
            }).ToList();
    }
}
