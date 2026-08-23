using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IMaterialMatchingService
{
    /// <summary>Serbest metni eşleştirme için normalize eder (büyük/küçük harf, Türkçe karakter,
    /// noktalama, birim eş anlamlıları — teknik ölçüler/rakamlar korunur).</summary>
    string Normalize(string? text);

    /// <summary>Belirli bir birim fiyat listesi içinde en iyi eşleşme adaylarını döner (skor azalan).</summary>
    Task<List<MaterialMatchCandidateDto>> FindCandidatesAsync(
        int unitPriceListId, string originalName, string? originalSpec, string? companyName, int maxResults = 5);

    /// <summary>Kullanıcı onayını (Evet/Manuel seçim) kalıcı alias olarak kaydeder.</summary>
    Task SaveAliasAsync(string? companyName, string aliasText, int unitPriceItemId, string? note = null);
}
