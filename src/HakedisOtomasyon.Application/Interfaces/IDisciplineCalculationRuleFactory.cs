using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.Interfaces;

/// <summary>Aktif disipline göre doğru IDisciplineCalculationRules implementasyonunu döner.</summary>
public interface IDisciplineCalculationRuleFactory
{
    IDisciplineCalculationRules GetRules(MechanicalDiscipline discipline);
}
