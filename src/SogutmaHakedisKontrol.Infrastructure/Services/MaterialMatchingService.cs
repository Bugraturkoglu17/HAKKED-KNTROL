using System.Text;
using System.Text.RegularExpressions;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Malzeme/hizmet adı normalizasyonu ve onaylı birim fiyat kataloğuna karşı eşleştirme motoru.
/// Öncelik sırası: 1) onaylı alias  2) birebir normalize eşleşme  3) fuzzy skorlama.
/// Teknik ölçü (3/8, 5/8, mm, kW vb.) farklıysa yüksek metinsel benzerlik olsa bile uyarı verir.
/// </summary>
public class MaterialMatchingService : IMaterialMatchingService
{
    private readonly AppDbContext _db;

    public MaterialMatchingService(AppDbContext db)
    {
        _db = db;
    }

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TechnicalTokenRegex = new(
        @"\d+\s*/\s*\d+(?:\s*/\s*\d+)?""?|\d+(?:[.,]\d+)?\s*(?:mm|cm|mt|m|kw|hp|w|lt|gl|gr|kg|amper|a)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            var mapped = c switch
            {
                'İ' or 'I' => 'i',
                'ı' => 'i',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ğ' or 'ğ' => 'g',
                'Ö' or 'ö' => 'o',
                'Ü' or 'ü' => 'u',
                ' ' => ' ', // non-breaking space (kaynak Excel'lerde sık görülüyor)
                _ when char.IsUpper(c) => char.ToLowerInvariant(c),
                _ => c,
            };
            if (mapped is '.' or ',' or ';' or ':' or '(' or ')' or '[' or ']')
                continue;
            sb.Append(mapped);
        }
        return WhitespaceRegex.Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Teknik ölçü/boyut belirten tokenleri çıkarır (3/8, 5/8", 10 mm, 50 w, ...).</summary>
    public static HashSet<string> ExtractTechnicalTokens(string normalizedText)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TechnicalTokenRegex.Matches(normalizedText))
            result.Add(WhitespaceRegex.Replace(m.Value, ""));
        return result;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[a.Length, b.Length];
    }

    public static double SimilarityRatio(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        int distance = LevenshteinDistance(a, b);
        int maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)distance / maxLen;
    }

    public async Task<List<MaterialMatchCandidateDto>> FindCandidatesAsync(
        int unitPriceListId, string originalName, string? originalSpec, string? companyName, int maxResults = 5)
    {
        var combined = $"{originalName} {originalSpec}".Trim();
        var normalized = Normalize(combined);
        if (string.IsNullOrEmpty(normalized)) return new List<MaterialMatchCandidateDto>();

        // 1) Onaylı alias (firma özel, sonra global)
        var normalizedAliasCandidates = new[] { companyName, null }.Distinct();
        foreach (var scope in normalizedAliasCandidates)
        {
            var alias = await _db.MaterialAliases
                .Where(a => a.NormalizedAlias == normalized && a.CompanyName == scope)
                .Include(a => a.UnitPriceItem)
                .FirstOrDefaultAsync();
            if (alias != null && alias.UnitPriceItem.IsActive)
            {
                return new List<MaterialMatchCandidateDto>
                {
                    ToCandidate(alias.UnitPriceItem, 1.0m)
                };
            }
        }

        // 2) Kataloğu tara (aktif kalemler), skorla
        var items = await _db.UnitPriceItems
            .Where(i => i.UnitPriceListId == unitPriceListId && i.IsActive)
            .ToListAsync();

        var technicalTokensSource = ExtractTechnicalTokens(normalized);

        var scored = items
            .Select(i => new
            {
                Item = i,
                Score = SimilarityRatio(normalized, i.NormalizedName)
            })
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .ToList();

        var result = new List<MaterialMatchCandidateDto>();
        foreach (var s in scored)
        {
            if (s.Score < 0.35) continue; // çok düşük skorları hiç önerme
            var candidate = ToCandidate(s.Item, (decimal)Math.Round(s.Score, 4));
            var technicalTokensTarget = ExtractTechnicalTokens(s.Item.NormalizedName);
            if (technicalTokensSource.Count > 0 && technicalTokensTarget.Count > 0 &&
                !technicalTokensSource.Overlaps(technicalTokensTarget))
            {
                candidate.SpecMismatchWarning = true;
            }
            result.Add(candidate);
        }
        return result;
    }

    private static MaterialMatchCandidateDto ToCandidate(UnitPriceItem item, decimal confidence) => new()
    {
        UnitPriceItemId = item.Id,
        MaterialName = item.MaterialName,
        Spec = item.Spec,
        Unit = item.Unit,
        Price = item.Price,
        Currency = item.Currency,
        Confidence = confidence,
    };

    public async Task SaveAliasAsync(string? companyName, string aliasText, int unitPriceItemId, string? note = null)
    {
        var normalized = Normalize(aliasText);
        if (string.IsNullOrEmpty(normalized)) return;

        var existing = await _db.MaterialAliases
            .FirstOrDefaultAsync(a => a.NormalizedAlias == normalized && a.CompanyName == companyName);

        if (existing != null)
        {
            existing.UnitPriceItemId = unitPriceItemId;
            existing.ApprovedByUser = true;
            existing.Note = note;
        }
        else
        {
            _db.MaterialAliases.Add(new MaterialAlias
            {
                CompanyName = companyName,
                AliasText = aliasText,
                NormalizedAlias = normalized,
                UnitPriceItemId = unitPriceItemId,
                ApprovedByUser = true,
                Note = note,
            });
        }
        await _db.SaveChangesAsync();
    }
}
