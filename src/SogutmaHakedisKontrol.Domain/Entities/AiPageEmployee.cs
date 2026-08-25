namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>Bir servis formu sayfasında AI'nın okuduğu tek bir personel çalışma kaydı.
/// Adam-saat matematiği burada değil, ManHoursCalculator'da (backend) yapılır.</summary>
public class AiPageEmployee
{
    public int Id { get; set; }
    public int PageId { get; set; }

    public string? NameRaw { get; set; }
    public string? StartTimeRaw { get; set; }   // "10:00" gibi ham metin
    public string? EndTimeRaw { get; set; }
    public TimeSpan? StartTime { get; set; }     // backend'de parse edilmiş hâli
    public TimeSpan? EndTime { get; set; }
    public decimal? HoursWorked { get; set; }    // backend hesaplanan (bitiş-başlangıç)
    public decimal? Confidence { get; set; }

    public AiDocumentPage Page { get; set; } = null!;
}
