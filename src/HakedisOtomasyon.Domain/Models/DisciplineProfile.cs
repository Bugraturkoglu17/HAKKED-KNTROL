using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Domain.Models;

public class DisciplineProfile
{
    public MechanicalDiscipline Discipline { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string RouteName { get; set; } = string.Empty;
    public string ThemeColor { get; set; } = string.Empty;
    public string DataFolderName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
