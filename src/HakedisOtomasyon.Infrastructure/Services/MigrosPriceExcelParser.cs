using ClosedXML.Excel;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Orijinal Migros birim fiyat Excel dosyasını okuyarak PriceItemDto listesi çıkarır.
/// Desteklenen sayfalar: YANGIN TESİSATI, HAVALANDIRMA, DİĞER
/// </summary>
public static class MigrosPriceExcelParser
{
    private static readonly string[] VariablePriceKeywords =
        ["liste", "iskonto", "piyasa", "fiyati", "fiyatı", "bvn", "s&p", "katalog", "catalog", "teklif", "özel"];

    private static readonly string[] PercentageKeywords =
        ["fittings", "montaj malzeme", "boru montaj", "%30", "%25", "%20", "malzeme toplam"];

    // ------------------------------------------------------------------ //
    //  GİRİŞ NOKTASI
    // ------------------------------------------------------------------ //
    public static PriceImportPreviewDto Parse(Stream stream)
    {
        var preview = new PriceImportPreviewDto();
        var items = new List<PriceItemDto>();
        var mainCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var wb = new XLWorkbook(stream);

        foreach (var ws in wb.Worksheets)
        {
            var name = ws.Name.Trim();
            try
            {
                if (name.Contains("YANGIN", StringComparison.OrdinalIgnoreCase))
                    ParseYanginSheet(ws, items, preview, mainCats, subCats);
                else if (name.Contains("HAVALANDIRMA", StringComparison.OrdinalIgnoreCase))
                    ParseHavalandirmaSheet(ws, items, preview, mainCats, subCats);
                else if (ContainsIgnoreTurkish(name, "DIGER") || ContainsIgnoreTurkish(name, "DİĞER"))
                    ParseDigerSheet(ws, items, preview, mainCats, subCats);
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"Sayfa '{name}': {ex.Message}");
                preview.ErrorCount++;
            }
        }

        // Finalize preview stats
        preview.Items = items;
        preview.TotalItems = items.Count(i => i.IsSelectable);
        preview.MainCategoryCount = mainCats.Count;
        preview.SubCategoryCount = subCats.Count;
        preview.FixedPriceCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.FixedPrice);
        preview.LaborOnlyCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.LaborOnly);
        preview.MaterialOnlyCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.MaterialOnly);
        preview.VariablePriceCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.VariablePrice);
        preview.PercentageBasedCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.PercentageBased);
        preview.MissingUnitCount = items.Count(i => i.IsSelectable && i.HasMissingUnit);

        return preview;
    }

    // ------------------------------------------------------------------ //
    //  YANGIN TESİSATI — A:PozNo  B:Açıklama  C:Birim  D:Malzeme  E:İşçilik
    // ------------------------------------------------------------------ //
    private static void ParseYanginSheet(
        IXLWorksheet ws, List<PriceItemDto> items, PriceImportPreviewDto preview,
        HashSet<string> mainCats, HashSet<string> subCats)
    {
        string? mainCat = null, subCat = null, subCat2 = null;
        string? lastUnit = null;

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            try
            {
                var desc = CellText(row, 2);
                if (string.IsNullOrWhiteSpace(desc)) continue;

                var pozNo = CellText(row, 1);
                var unit = CellText(row, 3);
                var matCell = row.Cell(4);
                var labCell = row.Cell(5);

                bool hasMatNum = TryGetDecimal(matCell, out var matPrice);
                bool hasLabNum = TryGetDecimal(labCell, out var labPrice);
                bool isVarMat = IsVariablePrice(matCell);
                bool isVarLab = IsVariablePrice(labCell);
                bool isPct = IsPercentageBased(desc);

                if (hasMatNum || hasLabNum || isVarMat || isVarLab || isPct)
                {
                    // SELECTABLE ITEM
                    if (!string.IsNullOrEmpty(unit)) lastUnit = unit;
                    bool missingUnit = string.IsNullOrEmpty(unit) && string.IsNullOrEmpty(lastUnit);
                    string effectiveUnit = !string.IsNullOrEmpty(unit) ? unit : (lastUnit ?? string.Empty);

                    var pt = isPct ? PriceType.PercentageBased
                        : (isVarMat || isVarLab) ? PriceType.VariablePrice
                        : (!hasMatNum && hasLabNum) ? PriceType.LaborOnly
                        : (hasMatNum && !hasLabNum) ? PriceType.MaterialOnly
                        : PriceType.FixedPrice;

                    var dto = BuildItem(
                        sheet: ws.Name, row: row.RowNumber(), pozNo: pozNo,
                        mainCat: mainCat, subCat: subCat, subCat2: subCat2,
                        desc: desc, unit: effectiveUnit,
                        mat: matPrice, lab: labPrice,
                        priceType: pt, missingUnit: missingUnit);
                    items.Add(dto);
                }
                else
                {
                    // CATEGORY ROW
                    UpdateCategoryStack(desc, ref mainCat, ref subCat, ref subCat2, mainCats, subCats);
                    lastUnit = null;
                }
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"YANGIN satır {row.RowNumber()}: {ex.Message}");
                preview.ErrorCount++;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  HAVALANDIRMA — A:Açıklama  B:Birim  C:Malzeme  D:İşçilik
    // ------------------------------------------------------------------ //
    private static void ParseHavalandirmaSheet(
        IXLWorksheet ws, List<PriceItemDto> items, PriceImportPreviewDto preview,
        HashSet<string> mainCats, HashSet<string> subCats)
    {
        string? mainCat = null, subCat = null, subCat2 = null;
        string? lastUnit = null;

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            try
            {
                var desc = CellText(row, 1);
                if (string.IsNullOrWhiteSpace(desc)) continue;

                var unit = CellText(row, 2);
                var matCell = row.Cell(3);
                var labCell = row.Cell(4);

                bool hasMatNum = TryGetDecimal(matCell, out var matPrice);
                bool hasLabNum = TryGetDecimal(labCell, out var labPrice);
                bool isVarMat = IsVariablePrice(matCell);
                bool isVarLab = IsVariablePrice(labCell);
                bool isPct = IsPercentageBased(desc);

                if (hasMatNum || hasLabNum || isVarMat || isVarLab || isPct)
                {
                    // Birim kalıtımı: boşsa önceki birimi kullan
                    if (!string.IsNullOrEmpty(unit)) lastUnit = unit;
                    bool missingUnit = string.IsNullOrEmpty(unit) && string.IsNullOrEmpty(lastUnit);
                    string effectiveUnit = !string.IsNullOrEmpty(unit) ? unit : (lastUnit ?? string.Empty);

                    var pt = isPct ? PriceType.PercentageBased
                        : (isVarMat || isVarLab) ? PriceType.VariablePrice
                        : (!hasMatNum && hasLabNum) ? PriceType.LaborOnly
                        : (hasMatNum && !hasLabNum) ? PriceType.MaterialOnly
                        : PriceType.FixedPrice;

                    var dto = BuildItem(
                        sheet: ws.Name, row: row.RowNumber(), pozNo: null,
                        mainCat: mainCat, subCat: subCat, subCat2: subCat2,
                        desc: desc, unit: effectiveUnit,
                        mat: matPrice, lab: labPrice,
                        priceType: pt, missingUnit: missingUnit);
                    items.Add(dto);
                }
                else
                {
                    UpdateCategoryStack(desc, ref mainCat, ref subCat, ref subCat2, mainCats, subCats);
                    lastUnit = null;
                }
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"HAVALANDIRMA satır {row.RowNumber()}: {ex.Message}");
                preview.ErrorCount++;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  DİĞER — A:PozNo  B:Açıklama  C:Miktar(skip)  D:Birim  E:Marka(skip)  F:Malzeme  G:İşçilik
    // ------------------------------------------------------------------ //
    private static void ParseDigerSheet(
        IXLWorksheet ws, List<PriceItemDto> items, PriceImportPreviewDto preview,
        HashSet<string> mainCats, HashSet<string> subCats)
    {
        string? mainCat = null, subCat = null, subCat2 = null;
        string? lastUnit = null;

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            try
            {
                var desc = CellText(row, 2);
                if (string.IsNullOrWhiteSpace(desc)) continue;

                var pozNo = CellText(row, 1);
                var unit = CellText(row, 4);
                var matCell = row.Cell(6);
                var labCell = row.Cell(7);

                bool hasMatNum = TryGetDecimal(matCell, out var matPrice);
                bool hasLabNum = TryGetDecimal(labCell, out var labPrice);
                bool isVarMat = IsVariablePrice(matCell);
                bool isVarLab = IsVariablePrice(labCell);
                bool isPct = IsPercentageBased(desc);

                if (hasMatNum || hasLabNum || isVarMat || isVarLab || isPct)
                {
                    if (!string.IsNullOrEmpty(unit)) lastUnit = unit;
                    bool missingUnit = string.IsNullOrEmpty(unit) && string.IsNullOrEmpty(lastUnit);
                    string effectiveUnit = !string.IsNullOrEmpty(unit) ? unit : (lastUnit ?? string.Empty);

                    var pt = isPct ? PriceType.PercentageBased
                        : (isVarMat || isVarLab) ? PriceType.VariablePrice
                        : (!hasMatNum && hasLabNum) ? PriceType.LaborOnly
                        : (hasMatNum && !hasLabNum) ? PriceType.MaterialOnly
                        : PriceType.FixedPrice;

                    var dto = BuildItem(
                        sheet: ws.Name, row: row.RowNumber(), pozNo: pozNo,
                        mainCat: mainCat, subCat: subCat, subCat2: subCat2,
                        desc: desc, unit: effectiveUnit,
                        mat: matPrice, lab: labPrice,
                        priceType: pt, missingUnit: missingUnit);
                    items.Add(dto);
                }
                else
                {
                    UpdateCategoryStack(desc, ref mainCat, ref subCat, ref subCat2, mainCats, subCats);
                    lastUnit = null;
                }
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"DİĞER satır {row.RowNumber()}: {ex.Message}");
                preview.ErrorCount++;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  YARDIMCI METODLAR
    // ------------------------------------------------------------------ //

    private static void UpdateCategoryStack(
        string desc,
        ref string? mainCat, ref string? subCat, ref string? subCat2,
        HashSet<string> mainCats, HashSet<string> subCats)
    {
        if (IsMainCategory(desc))
        {
            mainCat = desc;
            subCat = null;
            subCat2 = null;
            mainCats.Add(desc);
        }
        else
        {
            if (subCat == null)
            {
                subCat = desc;
                subCat2 = null;
                subCats.Add(desc);
            }
            else if (subCat2 == null)
            {
                subCat2 = desc;
            }
            else
            {
                // Yeni bir sub seviyesi → subCat güncelle, sub2 sıfırla
                subCat = desc;
                subCat2 = null;
                subCats.Add(desc);
            }
        }
    }

    private static PriceItemDto BuildItem(
        string sheet, int row, string? pozNo,
        string? mainCat, string? subCat, string? subCat2,
        string desc, string unit,
        decimal mat, decimal lab,
        PriceType priceType, bool missingUnit)
    {
        // DisplayName: "SubCat > Açıklama"
        string displayName = subCat2 != null ? $"{subCat2} > {desc}"
            : subCat != null ? $"{subCat} > {desc}"
            : desc;

        // InvoiceDescription: "SubCat - Açıklama"
        string invoiceDesc = subCat2 != null ? $"{subCat2} - {desc}"
            : subCat != null ? $"{subCat} - {desc}"
            : desc;

        var searchText = BuildSearchText(mainCat, subCat, subCat2, desc, unit, pozNo);

        return new PriceItemDto
        {
            SourceSheetName = sheet,
            PozNo = pozNo,
            MainCategory = mainCat,
            SubCategory = subCat,
            SubCategory2 = subCat2,
            Description = desc,
            DisplayName = displayName,
            InvoiceDescription = invoiceDesc,
            Unit = unit,
            MaterialPrice = mat,
            LaborPrice = lab,
            PriceType = priceType,
            IsSelectable = true,
            IsActive = !missingUnit, // birimi eksik olanlar başlangıçta pasif
            HasMissingUnit = missingUnit,
            IsManuallyAdded = false
        };
    }

    private static string BuildSearchText(
        string? mainCat, string? subCat, string? subCat2,
        string desc, string unit, string? pozNo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(mainCat)) parts.Add(mainCat);
        if (!string.IsNullOrEmpty(subCat)) parts.Add(subCat);
        if (!string.IsNullOrEmpty(subCat2)) parts.Add(subCat2);
        parts.Add(desc);
        if (!string.IsNullOrEmpty(unit)) parts.Add(unit);
        if (!string.IsNullOrEmpty(pozNo)) parts.Add(pozNo);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Satırın ana başlık olup olmadığını belirler.
    /// Heuristic: harflerin %65'inden fazlası büyük harf ise ana başlık.
    /// </summary>
    private static bool IsMainCategory(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 3) return false;
        var upper = letters.Count(char.IsUpper);
        return (double)upper / letters.Count >= 0.65;
    }

    private static bool IsVariablePrice(IXLCell cell)
    {
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number) return false;
        var text = cell.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return false;
        return VariablePriceKeywords.Any(k => text.Contains(k));
    }

    private static bool IsPercentageBased(string desc)
    {
        var lower = desc.ToLowerInvariant();
        return PercentageKeywords.Any(k => lower.Contains(k));
    }

    private static bool TryGetDecimal(IXLCell cell, out decimal value)
    {
        value = 0;
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number)
        {
            value = (decimal)cell.GetDouble();
            return true;
        }
        var text = cell.GetString()?.Trim().Replace(" ", string.Empty);
        if (string.IsNullOrEmpty(text)) return false;
        return decimal.TryParse(text, NumberStyles.Any, new CultureInfo("tr-TR"), out value)
            || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string CellText(IXLRow row, int col)
        => row.Cell(col).GetString()?.Trim() ?? string.Empty;

    private static bool ContainsIgnoreTurkish(string text, string search)
    {
        var n = NormalizeTurkish(text.ToUpperInvariant());
        var s = NormalizeTurkish(search.ToUpperInvariant());
        return n.Contains(s);
    }

    private static string NormalizeTurkish(string text)
        => text.Replace('Ç', 'C').Replace('Ğ', 'G').Replace('İ', 'I')
               .Replace('Ö', 'O').Replace('Ş', 'S').Replace('Ü', 'U');
}
