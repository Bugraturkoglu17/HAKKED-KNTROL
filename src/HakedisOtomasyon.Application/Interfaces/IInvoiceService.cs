using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IInvoiceService
{
    Task<List<InvoiceDto>> GetByFormIdAsync(int formId);
    Task<InvoiceDto?> GetByIdAsync(int id);
    Task<InvoiceDto> CreateAsync(InvoiceDto dto);
    Task<InvoiceDto> UpdateAsync(InvoiceDto dto);
    Task DeleteAsync(int id);
}
