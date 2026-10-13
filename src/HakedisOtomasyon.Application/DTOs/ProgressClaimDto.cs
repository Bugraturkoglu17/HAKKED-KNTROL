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

    /// <summary>Soğutma hakedişinde oluşturma sırasında seçilen hakediş türü (Glikol Kullanım vb.). Diğer disiplinlerde null.</summary>
    public CoolingClaimCategory? CoolingCategory { get; set; }

    /// <summary>Soğutma hakedişinde EUR fiyatlı kalemlerin TL'ye çevrildiği kur (TL/EUR); hakediş oluşturulurken istenir.</summary>
    public decimal? EurRate { get; set; }
    public DateTime? EurRateDate { get; set; }
    public string? EurRateSource { get; set; }   // "TCMB" | "Manuel"

    /// <summary>Soğutma hakediş türü etiketi (seçilmemişse boş).</summary>
    public string CoolingCategoryLabel => CoolingCategory?.DisplayName() ?? string.Empty;

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
