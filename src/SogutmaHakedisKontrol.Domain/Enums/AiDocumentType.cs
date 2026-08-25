namespace SogutmaHakedisKontrol.Domain.Enums;

/// <summary>GPT'nin bir sayfayı görsel olarak sınıflandırma sonucu.</summary>
public enum AiDocumentType
{
    Unknown = 0,
    ServiceForm = 1,               // SERVICE_FORM — Soğutma Malzeme / Servis Formu
    PeriodicMaintenanceForm = 2,   // PERIODIC_MAINTENANCE_FORM — Soğutma Ağır Bakım / Periyodik Bakım Formu
    Summary = 3,                   // SUMMARY — İcmal / Hakediş özet tablosu (Excel benzeri, form değil)
}
