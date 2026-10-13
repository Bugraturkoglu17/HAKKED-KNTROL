using System.Globalization;
using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Domain.Entities;

namespace HakedisOtomasyon.Infrastructure.ExcelExport;

/// <summary>
/// Soğutma "Glikol Kullanım" hakediş Excel çıktısı. İki sayfa:
/// 1) "GENEL ICMAL" — mağaza bazında özet: S/N | MAĞAZA KODU | MAĞAZA ADI | İL | FORMAT | TOPLAM FİYAT + GENEL TOPLAM.
///    Mağaza adı, ayrıntı sayfasındaki mağaza bloğuna köprüdür; TOPLAM FİYAT ayrıntı sayfasındaki GENEL TOPLAM hücresine bağlıdır.
/// 2) "MALZ HAKEDIS" — ayrıntı (onaylı örnek tabloya göre):
///   MAĞAZA KODU | MAĞAZA ADI | FORMAT | TARİH | FORM NO | MALZEME KODU | MALZEME ADI | ONAYA SUNULAN MİKTAR |
///   KESİLEN MİKTAR | FATURA DÖNEMİ | FİYAT | TOPLAM | GENEL TOPLAM
/// • Her servis formu bir satırdır; mağazalar alt alta, AYNI mağazanın formları yan yana (mağaza koduna göre sıralı).
/// • GENEL TOPLAM mağaza bloğu boyunca birleştirilmiş hücrededir.
/// • Alt satır: tüm mağazaların toplamı. Servis ücreti yoktur.
/// • KESİLEN MİKTAR boş bırakılır (onaylayan doldurur) ve doğrudan GENEL TOPLAM'dan düşer:
///   GENEL TOPLAM = TOPLAM'ların toplamı − kesilen miktar × fiyat (TOPLAM sütunu değişmez). İcmal de buna bağlı olduğundan güncellenir.
/// • FİYAT, EUR fiyatı × kur'dur (yuvarlanmadan); TOPLAM = miktar × fiyat (yuvarlanmadan, hücre 2 hane gösterir) — örn.
///   30 × 458,8895 = 13.766,685 → "13.766,69". Genel toplamlar yuvarlanmamış satırların toplamıdır (onaylı örnek tabloda
///   71.127,87 — satırlar tek tek yuvarlanıp toplansaydı 71.127,88 olurdu).
/// </summary>
public static class GlycolClaimExcelBuilder
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private const string CurrencyFormat = "\"₺\" #,##0.00";
    public const string DetailSheetName = "MALZ HAKEDIS";
    public const string SummarySheetName = CoolingSummarySheet.SheetName;

    // Sütun numaraları
    private const int ColStoreCode = 1, ColStoreName = 2, ColFormat = 3, ColDate = 4, ColFormNo = 5, ColItemCode = 6,
        ColItemName = 7, ColQty = 8, ColCut = 9, ColPeriod = 10, ColPrice = 11, ColTotal = 12, ColGrand = 13;

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
        var ws = wb.Worksheets.Add(DetailSheetName);
        ws.SetTabColor(XLColor.FromHtml("#339966"));
        ws.Style.Font.FontName = "Calibri";
        ws.Style.Font.FontSize = 11;

        WriteHeader(ws);

        var period = GlycolClaim.PeriodName(claim.Month);
        int r = 2;
        var blocks = new List<(Store Store, int FirstRow)>();

        foreach (var storeGroup in rows.GroupBy(x => x.Store.Code, StringComparer.OrdinalIgnoreCase))
        {
            int first = r;
            foreach (var row in storeGroup)
            {
                WriteRow(ws, r, row, period);
                r++;
            }
            int last = r - 1;
            blocks.Add((storeGroup.First().Store, first));

            // GENEL TOPLAM: mağaza bloğu boyunca birleşik hücre. KESİLEN MİKTAR doğrudan buradan düşer:
            // (miktar − kesilen) × fiyat. Kesilen boşken TOPLAM'ların toplamına eşittir.
            // NOT: SUMPRODUCT(kesilen;fiyat) biçimi KULLANILMAZ — Excel, tek hücrelik aralıkta kesilen boşken #DEĞER! verir.
            var grand = ws.Range(first, ColGrand, last, ColGrand);
            if (last > first) grand.Merge();
            ws.Cell(first, ColGrand).FormulaA1 = last > first
                ? $"SUMPRODUCT(({Col(ColQty)}{first}:{Col(ColQty)}{last}-{Col(ColCut)}{first}:{Col(ColCut)}{last})*{Col(ColPrice)}{first}:{Col(ColPrice)}{last})"
                : $"({Col(ColQty)}{first}-{Col(ColCut)}{first})*{Col(ColPrice)}{first}";
            grand.Style.NumberFormat.Format = CurrencyFormat;
            grand.Style.Font.Bold = true;
            grand.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            grand.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        int lastDataRow = r - 1;
        WriteFooter(ws, r, lastDataRow, rows, claim);

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
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);

        CoolingSummarySheet.Write(wb, CoolingSummarySheet.PeriodText(claim.Month, claim.Year), RegionTitle(rows, claim) + " İCMALİ",
            DetailSheetName, ColStoreCode, ColGrand, blocks);
        WriteMissingSheet(wb, allForms);
        return ws;
    }

    private static string Col(int index) => XLHelper.GetColumnLetterFromNumber(index);

    private static void WriteHeader(IXLWorksheet ws)
    {
        string[] headers =
        {
            "MAĞAZA KODU", "MAĞAZA ADI", "FORMAT", "TARİH", "FORM NO", "MALZEME KODU", "MALZEME ADI",
            "ONAYA SUNULAN MİKTAR", "KESİLEN MİKTAR", "FATURA DÖNEMİ", "FİYAT", "TOPLAM", "GENEL TOPLAM",
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
        ws.Row(1).Height = 46;   // "ONAYA SUNULAN MİKTAR" üç satır
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

        var range = ws.Range(r, 1, r, ColGrand);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        foreach (var c in new[] { ColStoreCode, ColFormat, ColDate, ColFormNo, ColItemCode, ColQty, ColCut, ColPeriod })
            ws.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(r, ColQty).Style.Font.Bold = true;
    }

    private static void SetNumberOrText(IXLCell cell, string? text) => CoolingSummarySheet.SetNumberOrText(cell, text);

    /// <summary>Başlık hakediş türüne göre: "… SOĞUTMA GLİKOL KULLANIM" ya da "… SOĞUTMA GAZ KULLANIM".</summary>
    private static string RegionTitle(List<Row> rows, ProgressClaim claim)
        => CoolingSummarySheet.RegionPrefix(rows.Select(x => x.Store)) + GlycolClaim.TitleCore(claim.CoolingCategory);

    private static void WriteFooter(IXLWorksheet ws, int r, int lastDataRow, List<Row> rows, ProgressClaim claim)
    {
        var label = ws.Range(r, ColStoreCode, r, ColTotal);
        label.Merge();
        label.Value = RegionTitle(rows, claim) + " HAKEDİŞİ";
        label.Style.Font.Bold = true;
        label.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        label.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        var firstData = 2;
        ws.Cell(r, ColGrand).FormulaA1 = lastDataRow >= firstData ? $"SUM({Col(ColGrand)}{firstData}:{Col(ColGrand)}{lastDataRow})" : "0";

        var all = ws.Range(r, ColStoreCode, r, ColGrand);
        all.Style.Fill.BackgroundColor = XLColor.FromHtml("#CCFFCC");
        all.Style.Font.Bold = true;
        all.Style.Font.FontSize = 12;
        all.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        ws.Cell(r, ColGrand).Style.NumberFormat.Format = CurrencyFormat;
        ws.Cell(r, ColGrand).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(r, ColGrand).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
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
        // Tek fiyat varsa (glikol; ya da tüm gazlar aynı fiyatsa) hesap açıkça yazılır; farklı EUR fiyatları varsa genel not düşülür.
        var eurPrices = rows.Select(x => x.Item).Where(i => i.ForeignPrice.HasValue && i.ExchangeRate.HasValue)
            .Select(i => i.ForeignPrice!.Value).Distinct().Count();
        var note = eurPrices == 1
            ? $"Not: Birim fiyat = {sample.ForeignPrice!.Value.ToString("0.####", Tr)} EUR × EUR kuru {EurPricing.FormatRate(rate)} TL " +
              $"= {(sample.ForeignPrice.Value * rate).ToString("N2", Tr)} TL (kur{source}{datePart})."
            : $"Not: Birim fiyatlar = EUR fiyatı × EUR kuru {EurPricing.FormatRate(rate)} TL (kur{source}{datePart}).";
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
