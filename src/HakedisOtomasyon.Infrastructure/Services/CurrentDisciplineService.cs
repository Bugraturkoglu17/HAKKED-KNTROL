using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Uygulama içinde o an aktif olan mekanik disiplini tutan basit durum servisi.
/// Şu an sadece Cooling (Soğutma) aktif olduğu için varsayılan değer Cooling'dir.
/// </summary>
public class CurrentDisciplineService
{
    public MechanicalDiscipline CurrentDiscipline { get; set; } = MechanicalDiscipline.Cooling;
}
