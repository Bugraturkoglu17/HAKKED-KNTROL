using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.DTOs;

public class StoreDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;
    public string? Format { get; set; }
    public string? Region { get; set; }

    /// <summary>Mağazanın ili — Soğutma Bakım &amp; Onarım'da şehir içi / şehir dışı servis bedelini belirler.</summary>
    public string? City { get; set; }
}
