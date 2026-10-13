using System.Globalization;
using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Domain.Entities;

namespace HakedisOtomasyon.Infrastructure.ExcelExport;

/// <summary>
/// Soğutma "Glikol Kullanım" hakediş Excel çıktısı. Düzen (onaylı örnek tabloya göre):
///   MAĞAZA KODU | MAĞAZA ADI | FORMAT | TARİH | FORM NO | MALZEME KODU | MALZEME ADI | ONAYA SUNULAN MİKTAR |
///   KESİLEN MİKTAR | FATURA DÖNEMİ | FİYAT | TOPLAM | GENEL TOPLAM | ONAYLANAN MİKTAR
/// • Her servis formu bir satırdır; mağazalar alt alta, AYNI mağazanın formları yan yana (mağaza koduna göre sıralı).
/// • GENEL TOPLAM ve ONAYLANAN değerleri mağaza bloğu boyunca birleştirilmiş hücrelerdedir.
/// • Alt satır: tüm mağazaların toplamı. Servis ücreti yoktur.
/// • KESİLEN MİKTAR boş bırakılır (onaylayan doldurur); ONAYLANAN = GENEL TOPLAM − kesilen miktar × fiyat.
/// • FİYAT, EUR fiyatı × kur'dur (yuvarlanmadan); TOPLAM = miktar × fiyat (yuvarlanmadan, hücre 2 hane gösterir) — örn.
///   30 × 458,8895 = 13.766,685 → "13.766,69". Genel toplamlar yuvarlanmamış satırların toplamıdır (onaylı örnek tabloda
///   71.127,87 — satırlar tek tek yuvarlanıp toplansaydı 71.127,88 olurdu).
/// </summary>
public static class GlycolClaimExcelBuilder
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private const string CurrencyFormat = "\"₺\" #,##0.00";

    // Sütun numaraları
    private const int ColStoreCode = 1, ColStoreName = 2, ColFormat = 3, ColDate = 4, ColFormNo = 5, ColItemCode = 6,
        ColItemName = 7, ColQty = 8, ColCut = 9, ColPeriod = 10, ColPrice = 11, ColTotal = 12, ColGrand = 13, ColApproved = 14;

    public sealed record Row(ServiceForm Form, Store Store, ServiceItem Item);

    /// <summary>Çıktıya girecek satırlar: mağazası seçilmiş ve glikol kalemi olan formlar, mağaza koduna göre sıralı.</summary>
    public static List<Row> CollectRows(IEnumerable<ServiceForm> forms)
        => forms
            .Where(f => !f.IsArchived && f.Store is not null)
            .Select(f => new Row(f, f.Store!, f.ServiceItems.Where(i => i.ItemCode != null).OrderBy(i => i.SortOrder).FirstOrDefault()!))
            .Where(r => r.Item is not null)
            .OrderBy(r => r.Store.Code, Comparer<string>.Create(GlycolClaim.CompareStoreCodes))
            .ThenBy(r => r.Form.FormDate ?? DateTime.MaxValue)
            .ThenBy(r => r.Form.FormNumber, Comparer<string?>.Create((a, b) => CompareFormNumbers(a, b)))
            .ThenBy(r => r.Form.Id)
            .ToList();

    private static int CompareFormNumbers(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return string.IsNullOrWhiteSpace(a) == string.IsNullOrWhiteSpace(b) ? 0 : (string.IsNullOrWhiteSpace(a) ? 1 : -1);
        return long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var x)
               && long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            ? x.CompareTo(y)
            : string.Compare(a, b, Tr, CompareOptions.IgnoreCase);
    }

    /// <summary>Bir satırdaki eksik alanların açıklaması (boşsa satır eksiksizdir).</summary>
    public static string MissingFields(ServiceForm form, ServiceItem? item)
    {
        var missing = new List<string>();
        if (form.Store is null) missing.Add("mağaza");
        if (!form.FormDate.HasValue) missing.Add("tarih");
        if (string.IsNullOrWhiteSpace(form.FormNumber)) missing.Add("form no");
        if (item is null || item.Quantity <= 0m) missing.Add("miktar");
        return string.Join(", ", missing);
    }

    public static IXLWorksheet Build(XLWorkbook wb, ProgressClaim claim, IEnumerable<ServiceForm> forms)
    {
        var allForms = forms.Where(f => !f.IsArchived).ToList();
        var rows = CollectRows(allForms);
        var ws = wb.Worksheets.Add("Glikol Hakediş");
        ws.Style.Font.FontName = "Calibri";
        ws.Style.Font.FontSize = 11;

        WriteHeader(ws);

        var period = GlycolClaim.PeriodName(claim.Month);
        int r = 2;
        var blockTopRows = new List<int>();

        foreach (var storeGroup in rows.GroupBy(x => x.Store.Code, StringComparer.OrdinalIgnoreCase))
        {
            int first = r;
            foreach (var row in storeGroup)
            {
                WriteRow(ws, r, row, period);
                r++;
            }
            int last = r - 1;
            blockTopRows.Add(first);

            // GENEL TOPLAM + ONAYLANAN: mağaza bloğu boyunca birleşik hücre
            var grand = ws.Range(first, ColGrand, last, ColGrand);
            var approved = ws.Range(first, ColApproved, last, ColApproved);
            if (last > first) { grand.Merge(); approved.Merge(); }
            ws.Cell(first, ColGrand).FormulaA1 = $"SUM({Col(ColTotal)}{first}:{Col(ColTotal)}{last})";
            ws.Cell(first, ColApproved).FormulaA1 =
                $"{Col(ColGrand)}{first}-SUMPRODUCT({Col(ColCut)}{first}:{Col(ColCut)}{last},{Col(ColPrice)}{first}:{Col(ColPrice)}{last})";
            foreach (var rng in new[] { grand, approved })
            {
                rng.Style.NumberFormat.Format = CurrencyFormat;
                rng.Style.Font.Bold = true;
                rng.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                rng.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
            approved.Style.Font.FontColor = XLColor.FromHtml("#00B050");
        }

        int lastDataRow = r - 1;
        WriteFooter(ws, r, lastDataRow, rows);

        // Kur notu — hesabın nasıl yapıldığı Excel'de görünsün
        WriteRateNote(ws, r + 2, rows);

        // Sayfa düzeni
        ws.SheetView.FreezeRows(1);
        ws.Column(ColStoreCode).Width = 11;
        ws.Column(ColStoreName).Width = 40;
        ws.Column(ColFormat).Width = 14;
        ws.Column(ColDate).Width = 12;
        ws.Column(ColFormNo).Width = 10;
        ws.Column(ColItemCode).Width = 10;
        ws.Column(ColItemName).Width = 38;
        ws.Column(ColQty).Width = 13;
        ws.Column(ColCut).Width = 13;
        ws.Column(ColPeriod).Width = 13;
        ws.Column(ColPrice).Width = 14;
        ws.Column(ColTotal).Width = 16;
        ws.Column(ColGrand).Width = 18;
        ws.Column(ColApproved).Width = 18;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);

        WriteMissingSheet(wb, allForms);
        return ws;
    }

    private static string Col(int index) => XLHelper.GetColumnLetterFromNumber(index);

    private static void WriteHeader(IXLWorksheet ws)
    {
        string[] headers =
        {
            "MAĞAZA KODU", "MAĞAZA ADI", "FORMAT", "TARİH", "FORM NO", "MALZEME KODU", "MALZEME ADI",
            "ONAYA SUNULAN MİKTAR", "KESİLEN MİKTAR", "FATURA DÖNEMİ", "FİYAT", "TOPLAM", "GENEL TOPLAM", "ONAYLANAN MİKTAR",
        };
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#9DC3E6");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }
        var cut = ws.Cell(1, ColCut);
        cut.Style.Fill.BackgroundColor = XLColor.FromHtml("#C0504D");
        cut.Style.Font.FontColor = XLColor.White;
        var ok = ws.Cell(1, ColApproved);
        ok.Style.Fill.BackgroundColor = XLColor.FromHtml("#00B050");
        ok.Style.Font.FontColor = XLColor.White;
        ws.Row(1).Height = 32;
    }

    private static void WriteRow(IXLWorksheet ws, int r, Row row, string period)
    {
        var form = row.Form;
        var item = row.Item;

        SetNumberOrText(ws.Cell(r, ColStoreCode), row.Store.Code);
        ws.Cell(r, ColStoreName).Value = row.Store.Name;
        var format = ws.Cell(r, ColFormat);
        format.Value = row.Store.Format ?? string.Empty;
        format.Style.Font.FontColor = XLColor.Red;
        format.Style.Font.Bold = true;
        if (form.FormDate.HasValue)
        {
            ws.Cell(r, ColDate).Value = form.FormDate.Value;
            ws.Cell(r, ColDate).Style.DateFormat.Format = "dd.MM.yyyy";
        }
        SetNumberOrText(ws.Cell(r, ColFormNo), form.FormNumber);
        SetNumberOrText(ws.Cell(r, ColItemCode), item.ItemCode);
        ws.Cell(r, ColItemName).Value = item.Description;
        ws.Cell(r, ColQty).Value = item.Quantity;
        // KESİLEN MİKTAR bilerek boş
        ws.Cell(r, ColPeriod).Value = period;

        // FİYAT: EUR fiyatı × kur (yuvarlanmadan); EUR değilse kayıtlı birim fiyat
        decimal unit = item.ForeignPrice.HasValue && item.ExchangeRate.HasValue
            ? item.ForeignPrice.Value * item.ExchangeRate.Value
            : item.UnitPrice;
        ws.Cell(r, ColPrice).Value = unit;
        ws.Cell(r, ColPrice).Style.NumberFormat.Format = CurrencyFormat;
        ws.Cell(r, ColTotal).FormulaA1 = $"{Col(ColQty)}{r}*{Col(ColPrice)}{r}";
        ws.Cell(r, ColTotal).Style.NumberFormat.Format = CurrencyFormat;

        var range = ws.Range(r, 1, r, ColApproved);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        foreach (var c in new[] { ColStoreCode, ColFormat, ColDate, ColFormNo, ColItemCode, ColQty, ColCut, ColPeriod })
            ws.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(r, ColQty).Style.Font.Bold = true;
    }

    private static void SetNumberOrText(IXLCell cell, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        // Baştaki sıfırı olmayan tam sayılar sayı olarak yazılır (Excel'de sola yaslı metin uyarısı çıkmasın)
        if (long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && (t.Length == 1 || t[0] != '0'))
            cell.Value = n;
        else
            cell.Value = t;
    }

    private static void WriteFooter(IXLWorksheet ws, int r, int lastDataRow, List<Row> rows)
    {
        var regions = rows.Select(x => x.Store.Region).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Create(Tr, true)).ToList();
        var title = (regions.Count > 0 ? string.Join(" / ", regions) + " " : string.Empty)
                    + "MİGROS SATIŞ MAĞAZALARI SOĞUTMA GLİKOL KULLANIM HAKEDİŞİ";

        var label = ws.Range(r, ColStoreCode, r, ColTotal);
        label.Merge();
        label.Value = title;
        label.Style.Font.Bold = true;
        label.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        label.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        var firstData = 2;
        ws.Cell(r, ColGrand).FormulaA1 = lastDataRow >= firstData ? $"SUM({Col(ColGrand)}{firstData}:{Col(ColGrand)}{lastDataRow})" : "0";
        ws.Cell(r, ColApproved).FormulaA1 = lastDataRow >= firstData ? $"SUM({Col(ColApproved)}{firstData}:{Col(ColApproved)}{lastDataRow})" : "0";

        var all = ws.Range(r, ColStoreCode, r, ColApproved);
        all.Style.Fill.BackgroundColor = XLColor.FromHtml("#CCFFCC");
        all.Style.Font.Bold = true;
        all.Style.Font.FontSize = 12;
        all.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        foreach (var c in new[] { ColGrand, ColApproved })
        {
            ws.Cell(r, c).Style.NumberFormat.Format = CurrencyFormat;
            ws.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
        ws.Cell(r, ColApproved).Style.Font.FontColor = XLColor.FromHtml("#00B050");
        ws.Row(r).Height = 34;
    }

    private static void WriteRateNote(IXLWorksheet ws, int r, List<Row> rows)
    {
        var sample = rows.Select(x => x.Item).FirstOrDefault(i => i.ForeignPrice.HasValue && i.ExchangeRate.HasValue);
        if (sample is null) return;
        var rate = sample.ExchangeRate!.Value;
        var date = sample.ExchangeRateActualDate ?? sample.ExchangeRateDate;
        var source = string.IsNullOrWhiteSpace(sample.ExchangeRateSource) ? "" : $", {sample.ExchangeRateSource}";
        var datePart = date.HasValue ? $" {date.Value:dd.MM.yyyy}" : "";
        var note = $"Not: Birim fiyat = {sample.ForeignPrice!.Value.ToString("0.####", Tr)} EUR × EUR kuru {EurPricing.FormatRate(rate)} TL " +
                   $"= {(sample.ForeignPrice.Value * rate).ToString("N2", Tr)} TL (kur{source}{datePart}).";
        var cell = ws.Cell(r, ColStoreCode);
        cell.Value = note;
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = XLColor.FromHtml("#595959");
    }

    /// <summary>Çıktıya girmeyen ya da alanları eksik formları ayrı sayfada listeler (denetim için).</summary>
    private static void WriteMissingSheet(XLWorkbook wb, List<ServiceForm> allForms)
    {
        var problems = allForms
            .Select(f => (Form: f, Item: f.ServiceItems.FirstOrDefault(i => i.ItemCode != null), Missing: MissingFields(f, f.ServiceItems.FirstOrDefault(i => i.ItemCode != null))))
            .Where(x => x.Missing.Length > 0)
            .ToList();
        if (problems.Count == 0) return;

        var ws = wb.Worksheets.Add("Eksik Formlar");
        ws.Cell(1, 1).Value = "SERVİS FORMU DOSYASI";
        ws.Cell(1, 2).Value = "MAĞAZA";
        ws.Cell(1, 3).Value = "EKSİK BİLGİ";
        ws.Cell(1, 4).Value = "ANA TABLODA";
        ws.Range(1, 1, 1, 4).Style.Font.Bold = true;
        ws.Range(1, 1, 1, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE699");
        int r = 2;
        foreach (var p in problems)
        {
            ws.Cell(r, 1).Value = p.Form.OriginalFileName;
            ws.Cell(r, 2).Value = p.Form.Store is null ? "(seçilmedi)" : $"{p.Form.Store.Code} - {p.Form.Store.Name}";
            ws.Cell(r, 3).Value = p.Missing;
            ws.Cell(r, 4).Value = p.Form.Store is null || p.Item is null ? "Yok" : "Var (eksik alanlı)";
            r++;
        }
        ws.Columns().AdjustToContents();
    }
}
