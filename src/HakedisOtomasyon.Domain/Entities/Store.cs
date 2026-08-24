using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Domain.Entities;

public class Store
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;

    public ICollection<ServiceForm> ServiceForms { get; set; } = new List<ServiceForm>();
}
