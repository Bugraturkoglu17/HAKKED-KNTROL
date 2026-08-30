namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IManHoursCalculator
{
    /// <summary>Tek bir personelin çalışma süresi (saat). Gece yarısını geçen vardiyalar desteklenir.</summary>
    decimal? CalculateHours(TimeSpan? start, TimeSpan? end);

    /// <summary>
    /// Toplam adam-saat üzerinden ödenebilir adam-saati hesaplar: max(0, toplam - düşülecek_saat).
    /// Düşülecek saat normalde 4'tür; kullanıcı talebi: "sadece tek istisna 1 kişi giderse 2 toplam
    /// çalışma saatinden 2 saat düşülür" — tek kişilik (solo) ziyaretlerde 2 saat düşülür.
    /// Bu matematik AI tarafından değil, burada deterministic olarak yapılır.
    /// </summary>
    decimal CalculatePayableHours(decimal totalManHours, int employeeCount);
}
