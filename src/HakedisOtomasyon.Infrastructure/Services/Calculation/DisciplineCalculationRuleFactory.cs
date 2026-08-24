using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Infrastructure.Services.Calculation;

/// <summary>
/// Aktif disipline göre doğru IDisciplineCalculationRules implementasyonunu döner.
/// Yangın dışındaki disiplinler şu an no-op döndüğü için Yangın'a özel kurallar
/// (fittings/boya/BDKF/BDTX/Flexiva) yalnızca Discipline.Fire seçiliyken çalışır.
/// </summary>
public class DisciplineCalculationRuleFactory : IDisciplineCalculationRuleFactory
{
    private readonly FireCalculationRules _fire;
    private readonly HvacCalculationRules _hvac;
    private readonly ElevatorCalculationRules _elevator;
    private readonly CoolingCalculationRules _cooling;

    public DisciplineCalculationRuleFactory(
        FireCalculationRules fire,
        HvacCalculationRules hvac,
        ElevatorCalculationRules elevator,
        CoolingCalculationRules cooling)
    {
        _fire = fire;
        _hvac = hvac;
        _elevator = elevator;
        _cooling = cooling;
    }

    public IDisciplineCalculationRules GetRules(MechanicalDiscipline discipline) => discipline switch
    {
        MechanicalDiscipline.Fire     => _fire,
        MechanicalDiscipline.Hvac     => _hvac,
        MechanicalDiscipline.Elevator => _elevator,
        MechanicalDiscipline.Cooling  => _cooling,
        _ => _fire
    };
}
