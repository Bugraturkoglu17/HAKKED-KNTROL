using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Firmanın gönderdiği bir aylık hakediş dosyasının onaylı birim fiyatlarla kontrol kaydı.
/// </summary>
public class ProgressPaymentCheck
{
    public int Id { get; set; }
    public int UnitPriceListId { get; set; }

    public string CompanyName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ClaimTypeName { get; set; } = string.Empty; // ör. "SABİT FİYAT", "PERY BAKIM" (serbest metin, gelecekte çoğalır)

    /// <summary>Kullanıcının kontrol başında seçtiği hakediş türü — AI'nin baktığı alanları belirler. Eski kayıtlarda null.</summary>
    public HakedisCategory? Category { get; set; }
    /// <summary>Çok sayfalı akışta kaldığı aşama (fiyat/form/sonuç) — ProgressPaymentCheckStatus'tan bağımsız.</summary>
    public HakedisControlStage Stage { get; set; } = HakedisControlStage.CategorySelected;
    public int Year { get; set; }
    public int Month { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;   // ör. "Nisan 2026"

    public string OriginalFileName { get; set; } = string.Empty;
    public string OriginalFilePath { get; set; } = string.Empty; // orijinal dosyanın değiştirilmeden saklandığı kopya

    public decimal? ExchangeRateEur { get; set; }
    public DateTime? ExchangeRateEnteredAt { get; set; }

    public decimal CompanyTotal { get; set; }
    public decimal CalculatedTotal { get; set; }
    public decimal Difference { get; set; }

    public ProgressPaymentCheckStatus Status { get; set; } = ProgressPaymentCheckStatus.Taslak;

    public string? ControlledFilePath { get; set; } // export edilmiş kontrol edilmiş kopya

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    public UnitPriceList UnitPriceList { get; set; } = null!;
    public ICollection<ProgressPaymentCheckItem> Items { get; set; } = new List<ProgressPaymentCheckItem>();
}
