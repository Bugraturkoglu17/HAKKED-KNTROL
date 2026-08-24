using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.DTOs;

public class ProgressClaimDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public int Month { get; set; } = DateTime.Now.Month;
    public int Year { get; set; } = DateTime.Now.Year;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public ProgressClaimStatus Status { get; set; }
    public int CurrentStep { get; set; }
    public int? LastOpenedServiceFormId { get; set; }
    public ProgressClaimType ClaimType { get; set; } = ProgressClaimType.MaintenanceRepair;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;

    public string ClaimTypeLabel => ClaimType switch
    {
        ProgressClaimType.NewConstruction => "Yeni Yapım",
        ProgressClaimType.Renovation      => "Tadilat",
        _                                 => "Bakım / Onarım"
    };

    /// <summary>Servis formu yükleme zorunlu mu?</summary>
    public bool IsFormRequired => ClaimType == ProgressClaimType.MaintenanceRepair;

    public string StatusLabel => Status switch
    {
        ProgressClaimStatus.Taslak => "Taslak",
        ProgressClaimStatus.Isleniyor => "İşleniyor",
        ProgressClaimStatus.Tamamlandi => "Tamamlandı",
        ProgressClaimStatus.DisaAktarildi => "Dışa Aktarıldı",
        _ => "Bilinmiyor"
    };
    public int FormCount { get; set; }
    public int CompletedFormCount { get; set; }
    public decimal TotalAmount { get; set; }
}
