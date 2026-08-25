using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>
/// Mağaza kesinleştirme — öncelik sırası: 1) tam kod 2) normalize kod 3) tam ad 4) fuzzy ad 5) AI önerisi (manuel onay gerekir).
/// AI yalnızca aday üretir; kesinleştirme burada, mağaza ana listesine karşı yapılır.
/// </summary>
public interface IStoreMatchingService
{
    Task<StoreMatchResultDto> MatchAsync(string companyName, string region, string? codeRaw, string? nameRaw);
    string NormalizeCode(string? code);
    string NormalizeName(string? name);
}
