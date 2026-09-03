using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// PDF içindeki tek bir sayfanın (bir servis formu ya da bir periyodik bakım formu) AI analiz sonucu.
/// Her sayfanın durumu bağımsız saklanır — bir sayfanın hatası diğerlerini etkilemez (kontrollü retry).
/// </summary>
public class AiDocumentPage
{
    public int Id { get; set; }
    public int JobId { get; set; }

    public AiDocumentSource SourceKind { get; set; }   // ServiceForm dosyası mı, PeriodicMaintenance dosyası mı
    public int PageNumber { get; set; }

    public AiPageStatus Status { get; set; } = AiPageStatus.Pending;
    public AiDocumentType DocumentType { get; set; } = AiDocumentType.Unknown; // GPT sınıflandırması

    // ── Mağaza adayı (AI ham çıktısı) ────────────────────────────────────
    public string? StoreCodeRaw { get; set; }
    public string? StoreNameRaw { get; set; }
    public decimal? StoreConfidence { get; set; }

    // ── Mağaza kesinleştirme (backend) ───────────────────────────────────
    public int? MatchedStoreId { get; set; }
    public StoreMatchMethod StoreMatchMethod { get; set; } = StoreMatchMethod.None;

    public string? FormNumber { get; set; }
    public decimal? FormNumberConfidence { get; set; } // 0-1 — hakediş Excel'i ile eşleştirmede ana anahtar
    public DateTime? ServiceDate { get; set; }        // SERVICE_FORM
    public DateTime? MaintenanceDate { get; set; }    // PERIODIC_MAINTENANCE_FORM

    public string? DescriptionRaw { get; set; }
    public string? WorkPerformedRaw { get; set; }

    // ── Adam-saat (backend deterministic hesap, bkz. ManHoursCalculator) ──
    public decimal? FormTotalHoursRaw { get; set; }   // formda yazan "Toplam Saat" (varsa)
    public decimal? CalculatedManHours { get; set; }
    public decimal? PayableManHours { get; set; }
    public bool? FormTotalMatch { get; set; }

    // ── Kullanıcı düzeltmeleri (AI'nin yanlış/eksik okuduğu alanlar için) ──
    // Doluysa, ilgili karşılaştırma stratejisi AI'nin kendi okumasının YERİNE bunu kullanır — kör bir
    // onay değil, gerçek bir düzeltilmiş ölçüm/okumadır; sonuç (Uygun/Uygun Değil) yine otomatik hesaplanır.
    public decimal? UserCorrectedPayableManHours { get; set; }
    public string? UserCorrectedManHoursNote { get; set; }
    public DateTime? UserCorrectedManHoursAt { get; set; }

    public string? UserCorrectedStoreRaw { get; set; }  // hem kod hem ad karşılaştırmasında kullanılır
    public string? UserCorrectedStoreNote { get; set; }
    public DateTime? UserCorrectedStoreAt { get; set; }

    // ── Periyodik bakım + servis çakışma sonucu ──────────────────────────
    public bool ServiceFeeRejectedDueToMaintenance { get; set; }

    public string? RawResponseJson { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    public bool RequiresManualReview { get; set; }
    public string? ManualReviewReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ProcessedAt { get; set; }

    public AiAnalysisJob Job { get; set; } = null!;
    public ICollection<AiPageEmployee> Employees { get; set; } = new List<AiPageEmployee>();
    public ICollection<AiPageMaterial> Materials { get; set; } = new List<AiPageMaterial>();
}
