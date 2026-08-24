using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

public class InvoiceService : IInvoiceService
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IFileCleanupService _cleanup;

    public InvoiceService(AppDbContext db, IFileStorageService fileStorage, IFileCleanupService cleanup)
    {
        _db = db;
        _fileStorage = fileStorage;
        _cleanup = cleanup;
    }

    public async Task<List<InvoiceDto>> GetByFormIdAsync(int formId)
    {
        return await _db.Invoices
            .Where(i => i.ServiceFormId == formId && !i.IsArchived)
            .OrderBy(i => i.UploadedAt)
            .Select(i => new InvoiceDto
            {
                Id = i.Id, ServiceFormId = i.ServiceFormId, FileName = i.FileName,
                FilePath = i.FilePath, Description = i.Description,
                InvoiceAmount = i.InvoiceAmount, Quantity = i.Quantity,
                MarkupRate = i.MarkupRate, CalculatedTotal = i.CalculatedTotal,
                UploadedAt = i.UploadedAt
            }).ToListAsync();
    }

    public async Task<InvoiceDto?> GetByIdAsync(int id)
    {
        var i = await _db.Invoices.FindAsync(id);
        if (i is null) return null;
        return new InvoiceDto
        {
            Id = i.Id, ServiceFormId = i.ServiceFormId, FileName = i.FileName,
            FilePath = i.FilePath, FileUrl = i.FilePath is not null ? _fileStorage.GetFileUrl(i.FilePath) : null,
            Description = i.Description, InvoiceAmount = i.InvoiceAmount,
            Quantity = i.Quantity, MarkupRate = i.MarkupRate,
            CalculatedTotal = i.CalculatedTotal, UploadedAt = i.UploadedAt
        };
    }

    public async Task<InvoiceDto> CreateAsync(InvoiceDto dto)
    {
        dto.Recalculate();
        var entity = new Invoice
        {
            ServiceFormId = dto.ServiceFormId,
            FileName = dto.FileName,
            FilePath = dto.FilePath,
            Description = dto.Description,
            InvoiceAmount = dto.InvoiceAmount,
            Quantity = dto.Quantity,
            MarkupRate = dto.MarkupRate,
            CalculatedTotal = dto.CalculatedTotal
        };
        _db.Invoices.Add(entity);
        await _db.SaveChangesAsync();
        dto.Id = entity.Id;
        dto.UploadedAt = entity.UploadedAt;
        return dto;
    }

    public async Task<InvoiceDto> UpdateAsync(InvoiceDto dto)
    {
        var entity = await _db.Invoices.FindAsync(dto.Id)
            ?? throw new InvalidOperationException($"Fatura bulunamadı: {dto.Id}");
        dto.Recalculate();
        entity.Description = dto.Description;
        entity.InvoiceAmount = dto.InvoiceAmount;
        entity.Quantity = dto.Quantity;
        entity.MarkupRate = dto.MarkupRate;
        entity.CalculatedTotal = dto.CalculatedTotal;
        entity.FileName = dto.FileName;
        entity.FilePath = dto.FilePath;
        await _db.SaveChangesAsync();
        return dto;
    }

    public async Task DeleteAsync(int id)
    {
        // ArchiveRemovedInvoiceAsync: dosyayı arşive taşır + entity'yi DB'den hard delete eder
        await _cleanup.ArchiveRemovedInvoiceAsync(id);
    }
}
