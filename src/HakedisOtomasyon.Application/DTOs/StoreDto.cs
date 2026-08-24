using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.DTOs;

public class StoreDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;
}
