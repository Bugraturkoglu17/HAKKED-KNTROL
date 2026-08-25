using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Mağaza ana listesi Excel'ini okur. En az mağaza kodu + mağaza adı kolonları zorunludur;
/// bölge/şehir/adres varsa kullanılır. Kolon başlıkları esnek eşleştirilir.
/// </summary>
public static class StoreExcelParser
{
    public static StoreImportPreviewDto Parse(Stream stream, string fileName)
    {
        var preview = new StoreImportPreviewDto();
        var items = new List<StoreDto>();

        using var wb = new XLWorkbook(stream);

        foreach (var ws in wb.Worksheets)
        {
            if (ws.IsEmpty()) continue;
            try
            {
                ParseSheet(ws, items, preview);
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"Sayfa '{ws.Name}': {ex.Message}");
                preview.ErrorCount++;
            }
        }

        preview.Items = items;
        preview.TotalStores = items.Count;

        var dupGroups = items.GroupBy(i => i.Code.Trim().ToUpperInvariant()).Where(g => g.Count() > 1).ToList();
        preview.DuplicateCodeCount = dupGroups.Count;
        preview.DuplicateCodes = dupGroups.Select(g => g.Key).Take(20).ToList();

        if (preview.TotalStores == 0 && preview.Errors.Count == 0)
            preview.Errors.Add("Excel içinde mağaza bulunamadı. Beklenen kolonlar: Mağaza Kodu, Mağaza Adı.");

        return preview;
    }

    private static void ParseSheet(IXLWorksheet ws, List<StoreDto> items, StoreImportPreviewDto preview)
    {
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int searchLimit = Math.Min(10, lastRow);

        IXLRow? headerRow = null;
        int colCode = 0, colName = 0, colRegion = 0, colCity = 0, colAddress = 0;

        for (int r = 1; r <= searchLimit; r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;

            int kod = 0, ad = 0, bolge = 0, sehir = 0, adres = 0;
            foreach (var cell in row.CellsUsed())
            {
                var norm = NormalizeHeader(cell.GetString());
                if (norm.Length == 0) continue;
                var col = cell.Address.ColumnNumber;

                if (norm.Contains("magazakodu") || norm.Contains("isyerino") || norm == "kod") kod = col;
                else if (norm.Contains("magazaadi") || norm.Contains("isyeriadi") || norm == "ad" || norm == "unvan") ad = col;
                else if (norm.Contains("bolge")) bolge = col;
                else if (norm.Contains("sehir") || norm.Contains("il") == true && norm.Length <= 6) sehir = col;
                else if (norm.Contains("adres")) adres = col;
            }

            if (kod > 0 && ad > 0)
            {
                headerRow = row;
                colCode = kod; colName = ad; colRegion = bolge; colCity = sehir; colAddress = adres;
                preview.Errors.Clear(); // önceki sayfalardan biriken "bulunamadı" hatası varsa temizle
                break;
            }
        }

        if (headerRow is null)
        {
            preview.Errors.Add($"Sayfa '{ws.Name}': başlık satırı bulunamadı (Mağaza Kodu / Mağaza Adı kolonları gerekli).");
            preview.ErrorCount++;
            return;
        }

        int startRow = headerRow.RowNumber() + 1;
        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() >= startRow))
        {
            var code = CellText(row, colCode);
            var name = CellText(row, colName);
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name)) continue;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue; // eksik satır, atla

            items.Add(new StoreDto
            {
                Code = code.Trim(),
                Name = name.Trim(),
                StoreRegion = colRegion > 0 ? NullIfEmpty(CellText(row, colRegion)) : null,
                City = colCity > 0 ? NullIfEmpty(CellText(row, colCity)) : null,
                Address = colAddress > 0 ? NullIfEmpty(CellText(row, colAddress)) : null,
                IsActive = true,
            });
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string CellText(IXLRow row, int col)
        => col > 0 ? (row.Cell(col).GetString()?.Trim() ?? string.Empty) : string.Empty;

    private static string NormalizeHeader(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input.Trim())
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
                _ when char.IsUpper(c) => char.ToLowerInvariant(c),
                _ => c,
            };
            if (mapped is ' ' or '.' or '(' or ')' or '-' or '_' or '/')
                continue;
            sb.Append(mapped);
        }
        return sb.ToString();
    }
}
