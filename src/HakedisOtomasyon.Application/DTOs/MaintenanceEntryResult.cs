namespace HakedisOtomasyon.Application.DTOs;

/// <summary>
/// Soğutma Bakım &amp; Onarım: bir forma kalem ekleme sonucu.
/// </summary>
/// <param name="Form">Kalemin eklendiği form kaydı (güncel hâli).</param>
/// <param name="ItemId">Eklenen/miktarı artırılan kalem; yalnız form + servis bedeli oluşturulduysa null.</param>
/// <param name="Merged">Kalem formda zaten vardı, miktarı artırıldı.</param>
/// <param name="Fee">Otomatik servis bedelinin durumu (eklendi / zaten vardı / il bilinmiyor ...).</param>
public sealed record MaintenanceEntryResult(ServiceFormDto Form, int? ItemId, bool Merged, MaintenanceClaim.ServiceFeeResult Fee);
