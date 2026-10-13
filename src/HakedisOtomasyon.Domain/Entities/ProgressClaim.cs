using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Domain.Entities;

public class ProgressClaim
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public int Year { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ProgressClaimStatus Status { get; set; } = ProgressClaimStatus.Taslak;
    public int CurrentStep { get; set; } = 0;
    public int? LastOpenedServiceFormId { get; set; }
    public ProgressClaimType ClaimType { get; set; } = ProgressClaimType.MaintenanceRepair;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;

    /// <summary>Soğutma hakedişinde oluşturma sırasında seçilen hakediş türü (Glikol Kullanım vb.). Diğer disiplinlerde null.</summary>
    public CoolingClaimCategory? CoolingCategory { get; set; }

    /// <summary>Soğutma hakedişinde EUR fiyatlı kalemlerin TL'ye çevrildiği kur (TL/EUR); hakediş oluşturulurken istenir.</summary>
    public decimal? EurRate { get; set; }
    public DateTime? EurRateDate { get; set; }
    public string? EurRateSource { get; set; }   // "TCMB" | "Manuel"

    public ICollection<ServiceForm> ServiceForms { get; set; } = new List<ServiceForm>();
    public ICollection<ExportLog> ExportLogs { get; set; } = new List<ExportLog>();
}
