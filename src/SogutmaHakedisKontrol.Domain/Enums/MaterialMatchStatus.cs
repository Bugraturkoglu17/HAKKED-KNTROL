namespace SogutmaHakedisKontrol.Domain.Enums;

public enum MaterialMatchStatus
{
    Unmatched = 0,       // Birim fiyat bulunamadı (kırmızı)
    LearnedAlias = 1,    // Daha önce onaylanmış alias (yeşil)
    Exact = 2,            // Normalize edilmiş isim birebir eşleşti (yeşil)
    FuzzyPending = 3,     // Tahmini eşleşme, kullanıcı onayı bekliyor (turuncu)
    ManuallyMatched = 4,  // Kullanıcı arama ekranından elle seçti (yeşil, bu kontrol için)
}
