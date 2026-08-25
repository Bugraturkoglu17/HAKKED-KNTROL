namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>Bir servis formu sayfasında AI'nın okuduğu tek bir malzeme kalemi.
/// Ham ve normalize edilmiş ad ayrı tutulur; belirsiz miktar asla uydurulmaz (null + manuel kontrol).</summary>
public class AiPageMaterial
{
    public int Id { get; set; }
    public int PageId { get; set; }

    public string RawName { get; set; } = string.Empty;
    public string? NormalizedName { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? Confidence { get; set; }
    public bool RequiresManualReview { get; set; }

    // ── Kullanıcı düzeltmesi (orijinal AI değeri asla üzerine yazılmaz) ──
    public decimal? UserCorrectedQuantity { get; set; }
    public string? UserCorrectedUnit { get; set; }
    public DateTime? UserCorrectedAt { get; set; }
    public string? CorrectionNote { get; set; }

    public AiDocumentPage Page { get; set; } = null!;
}
