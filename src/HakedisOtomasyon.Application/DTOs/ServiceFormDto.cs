using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.DTOs;

public class ServiceFormDto
{
    public int Id { get; set; }
    public int ProgressClaimId { get; set; }
    public int? StoreId { get; set; }
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
    public ServiceFormStatus Status { get; set; }
    public string StatusLabel => Status switch
    {
        ServiceFormStatus.Bekliyor => "Bekliyor",
        ServiceFormStatus.Isleniyor => "İşleniyor",
        ServiceFormStatus.EksikBilgi => "Eksik Bilgi",
        ServiceFormStatus.Tamamlandi => "Tamamlandı",
        ServiceFormStatus.DisaAktarildi => "Dışa Aktarıldı",
        _ => "Bilinmiyor"
    };
    public string? Notes { get; set; }
    public List<ServiceItemDto> ServiceItems { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();
    public decimal TotalAmount => ServiceItems.Sum(x => x.TotalPrice) + Invoices.Sum(x => x.CalculatedTotal);
}
