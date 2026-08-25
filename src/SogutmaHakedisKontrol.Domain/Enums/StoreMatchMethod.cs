namespace SogutmaHakedisKontrol.Domain.Enums;

/// <summary>Eşleştirme önceliği sırasıyla: 1) tam kod 2) normalize kod 3) tam ad 4) fuzzy ad 5) AI önerisi+onay.</summary>
public enum StoreMatchMethod
{
    None = 0,
    ExactCode = 1,
    NormalizedCode = 2,
    ExactName = 3,
    FuzzyName = 4,
    AiSuggestedConfirmed = 5,
    ManualReview = 6,
}
