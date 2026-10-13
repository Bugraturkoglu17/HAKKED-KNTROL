using System.Globalization;
using ClosedXML.Excel;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Domain.Entities;

namespace HakedisOtomasyon.Infrastructure.ExcelExport;

/// <summary>
/// Soğutma "Bakım &amp; Onarım" (servis ve bakım malzemeleri) ve "Kompresör Değişim" hakediş Excel çıktısı (aynı düzen,
/// başlık türe göre). İki sayfa:
/// 1) "GENEL ICMAL" — mağaza bazında özet (bkz. <see cref="CoolingSummarySheet"/>).
/// 2) "MALZ HAKEDIS" — ayrıntı (onaylı örnek tabloya göre), her KALEM bir satırdır:
///   SIRA NO | MAĞAZA KODU | MAĞAZA ADI | FORMAT | TARİH | BAKIM FORM NO | MALZEME KODU | MALZEME ADI | MALZEME TİPİ |
///   MİKTARI | BİRİMİ | FİYAT | TOPLAM | GENEL TOPLAM
/// • Mağazalar mağaza koduna göre sıralı; aynı mağazanın formları tarih → form no sırasıyla alt alta; bir formun kalemleri
///   ekrandaki sırasıyla (önce otomatik şehir içi/dışı servis bedeli, sonra adam/saat ve malzemeler).
/// • GENEL TOPLAM mağaza bloğu boyunca birleştirilmiş hücrededir (= mağazanın TOPLAM'larının toplamı).
/// • MALZEME TİPİ yalnızca fiyat listesinde tipi olan kalemlerde yazılır; olmayanlarda BOŞ bırakılır.
/// • FİYAT: EUR fiyatlı kalemde EUR fiyatı × hakediş EUR kuru (yuvarlanmadan); TOPLAM = miktar × fiyat (formül).
/// "ÇALIŞMA" ve "Mağazalar" yardımcı sayfaları ÜRETİLMEZ — ad, tip, birim ve fiyat doğrudan satıra yazılır.
/// </summary>
public static class MaintenanceClaimExcelBuilder
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private const string MoneyFormat = "#,##0.00\" TL\"";
    private const string DateFormat = "[$-41F]d mmmm yy";   // 23 Temmuz 26
    public const string DetailSheetName = "MALZ HAKEDIS";
    public const string SummarySheetName = CoolingSummarySheet.SheetName;
    /// <summary>Ayrıntı tablosunun son sütunu (GENEL TOPLAM) — sağına fatura görselleri dizilirken kullanılır.</summary>
    public const int LastTableColumn = ColGrand;

    private const int ColNo = 1, ColStoreCode = 2, ColStoreName = 3, ColFormat = 4, ColDate = 5, ColFormNo = 6, ColItemCode = 7,
        ColItemName = 8, ColItemType = 9, ColQty = 10, ColUnit = 11, ColPrice = 12, ColTotal = 13, ColGrand = 14;

    private const int TitleRow = 1, HeaderRow = 2, SpacerRow = 3, FirstDataRow = 4;

    public sealed record Row(ServiceForm Form, Store Store, ServiceItem Item);

    /// <summary>Çıktıya girecek satırlar: mağazası seçilmiş formların tüm kalemleri.</summary>
    public static List<Row> CollectRows(IEnumerable<ServiceForm> forms)
        => forms
            .Where(f => !f.IsArchived && f.Store is not null)
            .OrderBy(f => f.Store!.Code, Comparer<string>.Create(GlycolClaim.CompareStoreCodes))
            .ThenBy(f => f.FormDate ?? DateTime.MaxValue)
            .ThenBy(f => f.FormNumber, Comparer<string?>.Create(CompareFormNumbers))
            .ThenBy(f => f.Id)
            .SelectMany(f => f.ServiceItems.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).Select(i => new Row(f, f.Store!, i)))
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

    /// <summary>Bir formdaki eksik alanların açıklaması (boşsa form eksiksizdir).</summary>
    public static string MissingFields(ServiceForm form)
    {
        var missing = new List<string>();
        if (form.Store is null) missing.Add("mağaza");
        if (!form.FormDate.HasValue) missing.Add("tarih");
        if (string.IsNullOrWhiteSpace(form.FormNumber)) missing.Add("bakım form no");
        if (form.ServiceItems.Count == 0) missing.Add("kalem");
        else if (form.Store is not null && !form.ServiceItems.Any(i => MaintenanceClaim.IsServiceFee(i.ItemCode, i.Description))) missing.Add("servis bedeli");
        return string.Join(", ", missing);
    }

    /// <summary>Kalemin TL birim fiyatı: EUR kalemde EUR fiyatı × kur (yuvarlanmadan), değilse kayıtlı birim fiyat.</summary>
    public static decimal UnitPriceTl(ServiceItem item)
        => item.ForeignPrice.HasValue && item.ExchangeRate.HasValue ? item.ForeignPrice.Value * item.ExchangeRate.Value : item.UnitPrice;

    /// <summary>
    /// MALZEME ADI / MALZEME TİPİ. Fiyat listesinden eklenen kalemde temiz ad ayrıca saklıdır (ExportDescription) — ekrandaki
    /// açıklama "KATEGORİ - ad tip" biçimindedir. Saklı ad yoksa (elle girilen kalem) açıklamadan ayrıştırılır.
    /// </summary>
    public static (string Name, string Type) NameAndType(ServiceItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ExportDescription)) return MaintenanceClaim.SplitName(item.Description, item.ItemType);
        var name = item.ExportDescription.Trim();
        var type = (item.ItemType ?? string.Empty).Trim();
        return (name, string.Equals(type, name, StringComparison.OrdinalIgnoreCase) ? string.Empty : type);
    }

    private static string Col(int index) => XLHelper.GetColumnLetterFromNumber(index);

    public static IXLWorksheet Build(XLWorkbook wb, ProgressClaim claim, IEnumerable<ServiceForm> forms)
    {
        var allForms = forms.Where(f => !f.IsArchived).ToList();
        var rows = CollectRows(allForms);
        var regionPrefix = CoolingSummarySheet.RegionPrefix(rows.Select(x => x.Store));
        // Başlık hakediş türüne göre: Bakım & Onarım → "… SERVİS VE BAKIM MALZEMELERİ", Kompresör Değişim → "… KOMPRESÖR TEMİN VE DEĞİŞİM"
        var titleCore = MaintenanceClaim.TitleCore(claim.CoolingCategory);

        var ws = wb.Worksheets.Add(DetailSheetName);
        ws.SetTabColor(XLColor.FromHtml("#339966"));
        ws.Style.Font.FontName = "Arial";
        ws.Style.Font.FontSize = 10;

        // Başlık
        var title = ws.Range(TitleRow, ColNo, TitleRow, ColGrand);
        title.Merge();
        title.Value = regionPrefix + titleCore + " HAKEDİŞİ";
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 11;
        title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        title.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(TitleRow).Height = 22;

        WriteHeader(ws);

        int r = FirstDataRow, no = 1;
        var blocks = new List<(Store Store, int FirstRow)>();
        foreach (var storeGroup in rows.GroupBy(x => x.Store.Code, StringComparer.OrdinalIgnoreCase))
        {
            int first = r;
            foreach (var row in storeGroup)
            {
                WriteRow(ws, r, no++, row);
                r++;
            }
            int last = r - 1;
            blocks.Add((storeGroup.First().Store, first));

            // GENEL TOPLAM: mağaza bloğu boyunca birleşik hücre
            var grand = ws.Range(first, ColGrand, last, ColGrand);
            if (last > first) grand.Merge();
            ws.Cell(first, ColGrand).FormulaA1 = $"SUM({Col(ColTotal)}{first}:{Col(ColTotal)}{last})";
            grand.Style.NumberFormat.Format = MoneyFormat;
            grand.Style.Font.Bold = true;
            grand.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            grand.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            // Mağaza bloğu kalın çerçeveyle ayrılır
            ws.Range(first, ColNo, last, ColGrand).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            grand.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        }

        int lastDataRow = r - 1;
        WriteFooter(ws, r + 1, lastDataRow, regionPrefix + titleCore);
        WriteRateNote(ws, r + 3, rows);

        ws.SheetView.FreezeRows(SpacerRow);
        ws.Column(ColNo).Width = 6;
        ws.Column(ColStoreCode).Width = 9;
        ws.Column(ColStoreName).Width = 38;
        ws.Column(ColFormat).Width = 25;   // "SERVİS REYONLU M" sığsın
        ws.Column(ColDate).Width = 14;
        ws.Column(ColFormNo).Width = 12;
        ws.Column(ColItemCode).Width = 16;   // "FATURA +%10 M.K." / "%10 İSKONTOLU LİSTE FİYATI" (iki satıra sarılır)
        ws.Column(ColItemName).Width = 52;
        ws.Column(ColItemType).Width = 30;
        ws.Column(ColQty).Width = 11;
        ws.Column(ColUnit).Width = 11;
        ws.Column(ColPrice).Width = 16;
        ws.Column(ColTotal).Width = 16;
        ws.Column(ColGrand).Width = 20;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(TitleRow, HeaderRow);

        CoolingSummarySheet.Write(wb, CoolingSummarySheet.PeriodText(claim.Month, claim.Year), regionPrefix + titleCore + " İCMALİ",
            DetailSheetName, ColStoreCode, ColGrand, blocks);
        WriteMissingSheet(wb, allForms);
        return ws;
    }

    private static void WriteHeader(IXLWorksheet ws)
    {
        string[] headers =
        {
            "SIRA NO", "MAĞAZA KODU", "MAĞAZA ADI", "FORMAT", "TARİH", "BAKIM FORM NO", "MALZEME KODU", "MALZEME ADI", "MALZEME TİPİ",
            "MİKTARI", "BİRİMİ", "FİYAT", "TOPLAM", "GENEL TOPLAM",
        };
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(HeaderRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#99CCFF");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        }
        ws.Row(HeaderRow).Height = 38;

        // Başlığın altındaki ince yeşil ayırıcı satır
        var spacer = ws.Range(SpacerRow, ColNo, SpacerRow, ColGrand);
        spacer.Style.Fill.BackgroundColor = XLColor.FromHtml("#CCFFCC");
        spacer.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        spacer.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        ws.Row(SpacerRow).Height = 14;
    }

    private static void WriteRow(IXLWorksheet ws, int r, int no, Row row)
    {
        var form = row.Form;
        var item = row.Item;
        var (name, type) = NameAndType(item);

        ws.Cell(r, ColNo).Value = no;
        CoolingSummarySheet.SetNumberOrText(ws.Cell(r, ColStoreCode), row.Store.Code);
        ws.Cell(r, ColStoreCode).Style.Font.FontSize = 12;
        ws.Cell(r, ColStoreName).Value = row.Store.Name;
        var format = ws.Cell(r, ColFormat);
        format.Value = row.Store.Format ?? string.Empty;
        format.Style.Font.FontColor = XLColor.Red;
        format.Style.Font.Bold = true;
        format.Style.Font.FontSize = 12;
        if (form.FormDate.HasValue)
        {
            ws.Cell(r, ColDate).Value = form.FormDate.Value;
            ws.Cell(r, ColDate).Style.DateFormat.Format = DateFormat;
        }
        CoolingSummarySheet.SetNumberOrText(ws.Cell(r, ColFormNo), form.FormNumber);
        CoolingSummarySheet.SetNumberOrText(ws.Cell(r, ColItemCode), item.ItemCode);
        ws.Cell(r, ColItemName).Value = name;
        if (type.Length > 0) ws.Cell(r, ColItemType).Value = type;   // tipi olmayan kalemde hücre BOŞ kalır
        ws.Cell(r, ColQty).Value = item.Quantity;
        ws.Cell(r, ColUnit).Value = item.Unit ?? string.Empty;

        ws.Cell(r, ColPrice).Value = UnitPriceTl(item);
        ws.Cell(r, ColPrice).Style.NumberFormat.Format = MoneyFormat;
        ws.Cell(r, ColPrice).Style.Font.FontSize = 11;
        ws.Cell(r, ColTotal).FormulaA1 = $"{Col(ColQty)}{r}*{Col(ColPrice)}{r}";
        ws.Cell(r, ColTotal).Style.NumberFormat.Format = MoneyFormat;
        ws.Cell(r, ColTotal).Style.Font.Bold = true;
        ws.Cell(r, ColTotal).Style.Font.FontSize = 11;

        var range = ws.Range(r, ColNo, r, ColGrand);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        foreach (var c in new[] { ColPrice, ColTotal })
            ws.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        // Uzun kod etiketi ("%10 İSKONTOLU LİSTE FİYATI") iki satıra sarılır; satır ona göre yükselir
        ws.Cell(r, ColItemCode).Style.Alignment.WrapText = true;
        ws.Row(r).Height = (item.ItemCode?.Trim().Length ?? 0) > 16 ? 30 : 20;
    }

    private static void WriteFooter(IXLWorksheet ws, int r, int lastDataRow, string title)
    {
        var label = ws.Range(r, ColNo, r, ColTotal);
        label.Merge();
        label.Value = title + " HAKEDİŞİ";
        label.Style.Font.Bold = true;
        label.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        label.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Cell(r, ColGrand).FormulaA1 = lastDataRow >= FirstDataRow
            ? $"SUM({Col(ColGrand)}{FirstDataRow}:{Col(ColGrand)}{lastDataRow})" : "0";
        ws.Cell(r, ColGrand).Style.NumberFormat.Format = MoneyFormat;
        ws.Cell(r, ColGrand).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(r, ColGrand).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        var all = ws.Range(r, ColNo, r, ColGrand);
        all.Style.Fill.BackgroundColor = XLColor.FromHtml("#CCFFCC");
        all.Style.Font.Bold = true;
        all.Style.Font.FontSize = 12;
        all.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        ws.Row(r).Height = 30;
    }

    /// <summary>EUR fiyatlı kalem varsa hangi kurla TL'ye çevrildiği Excel'de görünsün.</summary>
    private static void WriteRateNote(IXLWorksheet ws, int r, List<Row> rows)
    {
        var sample = rows.Select(x => x.Item).FirstOrDefault(i => i.ForeignPrice.HasValue && i.ExchangeRate.HasValue);
        if (sample is null) return;
        var date = sample.ExchangeRateActualDate ?? sample.ExchangeRateDate;
        var source = string.IsNullOrWhiteSpace(sample.ExchangeRateSource) ? "" : $", {sample.ExchangeRateSource}";
        var datePart = date.HasValue ? $" {date.Value:dd.MM.yyyy}" : "";
        var cell = ws.Cell(r, ColNo);
        cell.Value = $"Not: EUR fiyatlı kalemler EUR kuru {EurPricing.FormatRate(sample.ExchangeRate!.Value)} TL ile TL'ye çevrilmiştir (kur{source}{datePart}).";
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = XLColor.FromHtml("#595959");
    }

    /// <summary>Çıktıya girmeyen ya da alanları eksik formları ayrı sayfada listeler (denetim için). Eksik yoksa sayfa oluşmaz.</summary>
    private static void WriteMissingSheet(XLWorkbook wb, List<ServiceForm> allForms)
    {
        var problems = allForms.Select(f => (Form: f, Missing: MissingFields(f))).Where(x => x.Missing.Length > 0).ToList();
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
            ws.Cell(r, 4).Value = p.Form.Store is null || p.Form.ServiceItems.Count == 0 ? "Yok" : "Var (eksik alanlı)";
            r++;
        }
        ws.Columns().AdjustToContents();
    }
}
