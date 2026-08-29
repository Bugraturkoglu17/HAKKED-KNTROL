namespace SogutmaHakedisKontrol.Domain.Enums;

public enum AiComparisonStatus
{
    Uygun = 1,
    UygunDegil = 2,
    Eksik = 3,
    // 4 (Fazla) kaldırıldı — hiçbir karşılaştırma stratejisi bu durumu hiç üretmiyordu, sürekli boş
    // duruyordu. Numarayı yeniden kullanma; eski satırlarda hâlâ Status=4 kalmış olabilir.
    ManuelKontrol = 5,
}
