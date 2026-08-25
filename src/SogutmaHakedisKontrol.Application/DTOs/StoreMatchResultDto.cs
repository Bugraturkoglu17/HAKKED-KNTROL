using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Application.DTOs;

/// <summary>Mağaza kesinleştirme sonucu — öncelik sırasına göre (tam kod → normalize kod → tam ad → fuzzy ad → AI önerisi).</summary>
public class StoreMatchResultDto
{
    public int? StoreId { get; set; }
    public string? StoreLabel { get; set; }
    public StoreMatchMethod Method { get; set; } = StoreMatchMethod.None;
    public decimal Confidence { get; set; }
    public bool RequiresManualReview => Method is StoreMatchMethod.None or StoreMatchMethod.ManualReview;
}
