namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Kullanıcının bir hakediş kalemi üzerinde aldığı karar (Düzelt/Geri Al/Yeni Kalem Ekle/
/// Bu fiyat doğrudur/Bu ürünle eşleştir) — kalıcı denetim izi. Sayfa yenilense de kaybolmaz.
/// </summary>
public class CheckItemActionLog
{
    public int Id { get; set; }
    public int ProgressPaymentCheckItemId { get; set; }
    public int ProgressPaymentCheckId { get; set; }

    public string Action { get; set; } = string.Empty; // Duzelt, GeriAl, YeniKalemEkle, BuFiyatDogru, Eslestir, TopluDuzelt
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Note { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
