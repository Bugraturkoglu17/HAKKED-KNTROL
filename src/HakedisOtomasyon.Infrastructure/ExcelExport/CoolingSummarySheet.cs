using System.Globalization;
using ClosedXML.Excel;
using HakedisOtomasyon.Domain.Entities;

namespace HakedisOtomasyon.Infrastructure.ExcelExport;

/// <summary>
/// Soğutma hakediş çıktılarının ortak "GENEL ICMAL" sayfası (Glikol Kullanım ve Bakım &amp; Onarım):
///   S/N | MAĞAZA KODU | MAĞAZA ADI | İL | FORMAT | TOPLAM FİYAT  + GENEL TOPLAM
/// Mağaza başına bir satır. Mağaza adı ve format, ayrıntı sayfasındaki mağaza bloğuna köprüdür; TOPLAM FİYAT ayrıntı
/// sayfasındaki mağazanın GENEL TOPLAM hücresine bağlıdır (orada bir değer değişince icmal kendiliğinden güncellenir).
/// </summary>
internal static class CoolingSummarySheet
{
    public const string SheetName = "GENEL ICMAL";
    private const string MoneyFormat = "#,##0.00\" TL\"";

    /// <param name="blocks">Mağaza ve ayrıntı sayfasında o mağazanın ilk satırı (GENEL TOPLAM hücresi o satırdadır).</param>
    /// <param name="detailLinkColumn">Köprünün gideceği sütun (ayrıntı sayfasında mağaza kodu).</param>
    /// <param name="detailGrandColumn">Ayrıntı sayfasındaki GENEL TOPLAM sütunu.</param>
    public static IXLWorksheet Write(XLWorkbook wb, string periodText, string title, string detailSheetName,
        int detailLinkColumn, int detailGrandColumn, IReadOnlyList<(Store Store, int FirstRow)> blocks)
    {
        const int cNo = 1, cCode = 2, cName = 3, cCity = 4, cFormat = 5, cTotal = 6;
        var ws = wb.Worksheets.Add(SheetName);
        ws.Position = 1;
        ws.Style.Font.FontName = "Arial";
        ws.Style.Font.FontSize = 11;
        var linkColor = XLColor.FromHtml("#0000FF");
        var detail = detailSheetName.Replace("'", "''");
        string Col(int index) => XLHelper.GetColumnLetterFromNumber(index);

        // 1. satır: dönem ("Haziran 26") — 2. satır: başlık
        var period = ws.Range(1, cNo, 1, cTotal);
        period.Merge();
        period.Value = periodText;
        period.Style.Font.Bold = true;
        period.Style.Font.FontColor = XLColor.Red;
        period.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var titleRange = ws.Range(2, cNo, 2, cTotal);
        titleRange.Merge();
        titleRange.Value = title;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 12;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(1, cNo, 2, cTotal).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;

        string[] headers = { "S/N", "MAĞAZA KODU", "MAĞAZA ADI", "İL", "FORMAT", "TOPLAM FİYAT" };
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(3, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#99CCFF");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        }
        ws.Row(3).Height = 34;

        int r = 4, no = 1;
        foreach (var (store, firstRow) in blocks)
        {
            ws.Cell(r, cNo).Value = no++;
            ws.Cell(r, cNo).Style.Font.Italic = true;
            SetNumberOrText(ws.Cell(r, cCode), store.Code);
            ws.Cell(r, cCode).Style.Font.FontSize = 14;

            // Mağaza adı ve format: ayrıntı sayfasındaki mağaza bloğuna köprü
            var target = $"#'{detail}'!{Col(detailLinkColumn)}{firstRow}";
            foreach (var (col, text) in new[] { (cName, store.Name), (cFormat, store.Format ?? string.Empty) })
            {
                if (text.Length == 0) continue;
                var cell = ws.Cell(r, col);
                cell.FormulaA1 = $"HYPERLINK(\"{target}\",\"{text.Replace("\"", "\"\"")}\")";
                cell.Style.Font.FontColor = linkColor;
                cell.Style.Font.Underline = XLFontUnderlineValues.Single;
            }
            ws.Cell(r, cCity).Value = store.City ?? string.Empty;

            ws.Cell(r, cTotal).FormulaA1 = $"'{detail}'!{Col(detailGrandColumn)}{firstRow}";
            ws.Cell(r, cTotal).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(r, cTotal).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            foreach (var c in new[] { cNo, cCode })
                ws.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            var line = ws.Range(r, cNo, r, cTotal);
            line.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            line.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            line.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            r++;
        }
        int lastStoreRow = r - 1;
        if (lastStoreRow >= 4)
        {
            ws.Range(3, cNo, lastStoreRow, cTotal).SetAutoFilter();
            ws.Range(3, cNo, lastStoreRow, cTotal).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        }

        r++; // boş satır
        var totalLabel = ws.Range(r, cNo, r, cFormat);
        totalLabel.Merge();
        totalLabel.Value = "GENEL TOPLAM :";
        totalLabel.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(r, cTotal).FormulaA1 = lastStoreRow >= 4 ? $"SUM({Col(cTotal)}4:{Col(cTotal)}{lastStoreRow})" : "0";
        ws.Cell(r, cTotal).Style.NumberFormat.Format = MoneyFormat;
        ws.Cell(r, cTotal).Style.Font.Bold = true;
        ws.Cell(r, cTotal).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        var totalRow = ws.Range(r, cNo, r, cTotal);
        totalRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#CCFFFF");
        totalRow.Style.Font.FontSize = 12;
        totalRow.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        totalRow.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        ws.Row(r).Height = 26;

        ws.Column(cNo).Width = 7;
        ws.Column(cCode).Width = 14;
        ws.Column(cName).Width = 44;
        ws.Column(cCity).Width = 20;
        ws.Column(cFormat).Width = 26;
        ws.Column(cTotal).Width = 18;
        ws.SheetView.FreezeRows(3);
        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 0);
        return ws;
    }

    /// <summary>Baştaki sıfırı olmayan tam sayılar sayı olarak yazılır (Excel'de "metin olarak saklanan sayı" uyarısı çıkmasın).</summary>
    public static void SetNumberOrText(IXLCell cell, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        if (long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && (t.Length == 1 || t[0] != '0'))
            cell.Value = n;
        else
            cell.Value = t;
    }

    /// <summary>Dönem metni: "Haziran 26".</summary>
    public static string PeriodText(int month, int year)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        var name = month is >= 1 and <= 12 ? tr.TextInfo.ToTitleCase(tr.DateTimeFormat.GetMonthName(month).ToLower(tr)) : string.Empty;
        return $"{name} {year % 100:00}".Trim();
    }

    /// <summary>Çıktıdaki mağazaların bölgeleri: "İÇ ANADOLU" (birden çoksa " / " ile); bölge yoksa boş.</summary>
    public static string RegionPrefix(IEnumerable<Store> stores)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        var regions = stores.Select(x => x.Region).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Create(tr, true)).ToList();
        return regions.Count > 0 ? string.Join(" / ", regions) + " " : string.Empty;
    }
}
