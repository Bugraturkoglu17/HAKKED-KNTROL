using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// "Soğutma Malzeme Birim Fiyatlar" / "Soğutma Sistemleri Bakım Bedelleri" tipi resmi birim fiyat
/// Excel'lerini okur. Gerçek dosya yapısı: MALZEME KODU, ÜRÜN TİPİ, MALZEME, MARKA, TİP, [YIL] BAZ FİYAT.
/// "İŞÇİLİKLER" kategorisindeki kalemler TL, diğerleri EUR bazlıdır (onaylı katalogda doğrulanmıştır).
/// Kolon başlıkları esnek eşleştirilir; yıl her değiştiğinde kod değişikliği gerekmez.
/// </summary>
public static class SogutmaPriceExcelParser
{
    public static UnitPriceImportPreviewDto Parse(Stream stream, string fileName)
    {
        var preview = new UnitPriceImportPreviewDto();
        var items = new List<UnitPriceItemDto>();

        using var wb = new XLWorkbook(stream);
        preview.SheetCount = wb.Worksheets.Count;

        foreach (var ws in wb.Worksheets)
        {
            if (ws.IsEmpty()) continue;
            try
            {
                ParseSheet(ws, fileName, items, preview);
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"Sayfa '{ws.Name}': {ex.Message}");
                preview.ErrorCount++;
            }
        }

        preview.Items = items;
        preview.TotalItems = items.Count;
        preview.EurItemCount = items.Count(i => i.Currency == "EUR");
        preview.TryItemCount = items.Count(i => i.Currency == "TRY");
        preview.MissingPriceCount = items.Count(i => i.Price <= 0);

        var dupGroups = items
            .GroupBy(i => (Name: i.MaterialName.Trim().ToUpperInvariant(), Spec: (i.Spec ?? "").Trim().ToUpperInvariant()))
            .Where(g => g.Count() > 1)
            .ToList();
        preview.DuplicateNameCount = dupGroups.Count;
        preview.DuplicateNames = dupGroups.Select(g => $"{g.Key.Name} {g.Key.Spec}".Trim()).Take(20).ToList();

        if (preview.TotalItems == 0 && preview.Errors.Count == 0)
            preview.Errors.Add("Excel içinde kalem bulunamadı. Beklenen kolonlar: Malzeme Kodu, Ürün Tipi, Malzeme, Marka, Tip, Fiyat.");

        return preview;
    }

    private static void ParseSheet(IXLWorksheet ws, string fileName, List<UnitPriceItemDto> items, UnitPriceImportPreviewDto preview)
    {
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int searchLimit = Math.Min(10, lastRow);

        IXLRow? headerRow = null;
        int colKod = 0, colUrunTipi = 0, colMalzeme = 0, colMarka = 0, colTip = 0, colFiyat = 0;

        for (int r = 1; r <= searchLimit; r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;

            int kod = 0, urunTipi = 0, malzeme = 0, marka = 0, tip = 0, fiyat = 0;
            foreach (var cell in row.CellsUsed())
            {
                var norm = NormalizeHeader(cell.GetString());
                if (norm.Length == 0) continue;
                var col = cell.Address.ColumnNumber;

                if (norm.Contains("malzemekodu")) kod = col;
                else if (norm.Contains("uruntipi") || norm.Contains("urunutipi")) urunTipi = col;
                else if (norm == "malzeme") malzeme = col;
                else if (norm == "marka") marka = col;
                else if (norm == "tip") tip = col;
                else if (norm.Contains("fiyat")) fiyat = col; // "2026 baz fiyat" -> içerir "fiyat"
            }

            if (malzeme > 0 && fiyat > 0)
            {
                headerRow = row;
                colKod = kod; colUrunTipi = urunTipi; colMalzeme = malzeme;
                colMarka = marka; colTip = tip; colFiyat = fiyat;
                preview.DebugMessages.Add($"Sayfa '{ws.Name}': başlık {r}. satırda bulundu.");
                break;
            }
        }

        if (headerRow is null)
        {
            preview.Errors.Add($"Sayfa '{ws.Name}': başlık satırı bulunamadı (Malzeme / Fiyat kolonları gerekli).");
            preview.ErrorCount++;
            return;
        }

        int startRow = headerRow.RowNumber() + 1;
        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() >= startRow))
        {
            preview.TotalRowsRead++;
            var malzemeAdi = CellText(row, colMalzeme);
            if (string.IsNullOrWhiteSpace(malzemeAdi)) continue; // boş satır / toplam satırı vb.

            var urunTipiTxt = CellText(row, colUrunTipi);
            var markaTxt = CellText(row, colMarka);
            var tipTxt = CellText(row, colTip);
            var kodTxt = CellText(row, colKod);

            decimal fiyat = 0;
            if (colFiyat > 0) TryGetDecimal(row.Cell(colFiyat), out fiyat);

            var isIscilik = NormalizeHeader(urunTipiTxt).Contains("iscilik");
            var currency = isIscilik ? "TRY" : "EUR";

            items.Add(new UnitPriceItemDto
            {
                ItemCode = string.IsNullOrWhiteSpace(kodTxt) ? null : kodTxt,
                Category = string.IsNullOrWhiteSpace(urunTipiTxt) ? null : urunTipiTxt.Trim(),
                MaterialName = malzemeAdi.Trim(),
                Brand = string.IsNullOrWhiteSpace(markaTxt) ? null : markaTxt.Trim(),
                Spec = string.IsNullOrWhiteSpace(tipTxt) ? null : tipTxt.Trim(),
                Unit = isIscilik ? "set" : null,
                Price = fiyat,
                Currency = currency,
                SourceFileName = fileName,
                SourceRowNumber = row.RowNumber(),
                IsActive = true,
            });
        }
    }

    private static string CellText(IXLRow row, int col)
        => col > 0 ? (row.Cell(col).GetString()?.Trim() ?? string.Empty) : string.Empty;

    private static bool TryGetDecimal(IXLCell cell, out decimal value)
    {
        value = 0;
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number)
        {
            value = (decimal)cell.GetDouble();
            return true;
        }
        var text = (cell.GetString() ?? string.Empty).Trim().Replace("€", "").Replace("TL", "", StringComparison.OrdinalIgnoreCase);
        return decimal.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

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
