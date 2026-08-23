namespace SogutmaHakedisKontrol.Domain.Enums;

public enum ProgressPaymentCheckStatus
{
    Taslak = 0,          // Excel yüklendi, henüz eşleştirme tamamlanmadı
    EslesmeBekliyor = 1, // Turuncu kalemler kullanıcı onayı bekliyor
    Tamamlandi = 2,       // Kontrol tamamlandı, sonuç/export hazır
}
