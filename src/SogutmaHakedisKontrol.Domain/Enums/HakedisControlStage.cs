namespace SogutmaHakedisKontrol.Domain.Enums;

/// <summary>Çok sayfalı hakediş kontrol akışında kaydın hangi aşamada olduğunu tutar.
/// Mevcut ProgressPaymentCheckStatus (fiyat eşleşme durumu) ile karışmaz — bu, sayfalar arası
/// kaldığı yerden devam edebilme (state persistence) içindir.</summary>
public enum HakedisControlStage
{
    CategorySelected = 0,
    ExcelUploaded = 1,
    PriceReviewInProgress = 2,
    PriceReviewCompleted = 3,
    FormReviewInProgress = 4,
    FormReviewCompleted = 5,
    ReadyForExport = 6,
    Exported = 7,
}
