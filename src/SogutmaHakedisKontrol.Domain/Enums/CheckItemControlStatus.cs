namespace SogutmaHakedisKontrol.Domain.Enums;

public enum CheckItemControlStatus
{
    KontrolGerekli = 0,        // Sarı — henüz işlenmedi / eşleşme bekliyor
    Uygun = 1,                  // Yeşil — fiyat/birim uyumlu
    OnayBekliyor = 2,           // Turuncu — tahmini eşleşme kullanıcı onayı bekliyor
    FiyatHatasi = 3,             // Kırmızı — eşleşti ama fiyat farkı var
    BirimFiyatBulunamadi = 4,    // Kırmızı — onaylı listede karşılığı yok
    BirimUyusmazligi = 5,        // Kırmızı — birim (adet/metre vb.) uyuşmuyor
    KontrolDisi = 6,              // Gri — kullanıcı bilerek kontrol dışı bıraktı
}
