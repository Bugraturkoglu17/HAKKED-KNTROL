using System.Text.RegularExpressions;
using ClosedXML.Excel;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using HakedisOtomasyon.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HakedisOtomasyon.Infrastructure.ExcelExport;

public class ExcelExportService : IExcelExportService
{
    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;
    private readonly IFileStorageService _fileStorage;
    private readonly IPdfPreviewService _pdfPreview;
    private readonly ILogger<ExcelExportService> _logger;
    private readonly IFileCleanupService _cleanup;
    private readonly IPosMappingService _posMapping;
    private readonly IDisciplineProfileService _disciplineProfiles;
    private readonly CurrentDisciplineService _currentDiscipline;
    private readonly IDisciplineCalculationRuleFactory _calculationRuleFactory;

    // Türkiye para formatı
    private static readonly System.Globalization.CultureInfo TrCulture =
        new("tr-TR");
    private const string MoneyFormat = "#,##0.00\" TL\"";
    private static readonly string[] TurkishMonths =
    {
        "", "OCAK", "ŞUBAT", "MART", "NİSAN", "MAYIS", "HAZİRAN",
        "TEMMUZ", "AĞUSTOS", "EYLÜL", "EKİM", "KASIM", "ARALIK"
    };

    public ExcelExportService(
        AppDbContext db, ISettingsService settings, IFileStorageService fileStorage,
        IPdfPreviewService pdfPreview, ILogger<ExcelExportService> logger, IFileCleanupService cleanup,
        IPosMappingService posMapping, IDisciplineProfileService disciplineProfiles,
        CurrentDisciplineService currentDiscipline, IDisciplineCalculationRuleFactory calculationRuleFactory)
    {
        _db = db; _settings = settings; _fileStorage = fileStorage;
        _pdfPreview = pdfPreview; _logger = logger; _cleanup = cleanup; _posMapping = posMapping;
        _disciplineProfiles = disciplineProfiles; _currentDiscipline = currentDiscipline;
        _calculationRuleFactory = calculationRuleFactory;
    }

    public async Task<string> ExportClaimAsync(int claimId, IProgress<string>? progress = null)
    {
        var claim = await _db.ProgressClaims
            .Include(c => c.ServiceForms).ThenInclude(f => f.Store)
            .Include(c => c.ServiceForms).ThenInclude(f => f.ServiceItems.OrderBy(i => i.SortOrder))
            .Include(c => c.ServiceForms).ThenInclude(f => f.Invoices.Where(i => !i.IsArchived))
            .FirstOrDefaultAsync(c => c.Id == claimId)
            ?? throw new InvalidOperationException($"Hakediş bulunamadı: {claimId}");

        // Hakediş kaydındaki firma adını kullan; boşsa uygulama ayarından al
        var companyName = !string.IsNullOrWhiteSpace(claim.CompanyName)
            ? claim.CompanyName
            : await _settings.GetAsync(SettingKeys.CompanyName, "Şirket Adı");

        // Görsel kalite ayarını oku
        var qualitySetting = await _settings.GetAsync(SettingKeys.ExcelImageQuality, "standard");
        var docQuality = ImageOptimizer.Preset.FromSettingValue(qualitySetting);

        // POS sistemi SADECE Yangın'a aittir — diğer disiplinlerde dosya aranmaz, tazelenmez.
        bool posEnabled = _currentDiscipline.CurrentDiscipline == MechanicalDiscipline.Fire;
        if (posEnabled)
            _posMapping.Reload();
        var posExportLog = new List<string>();

        progress?.Report("Excel workbook oluşturuluyor...");

        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Calibri";
        wb.Style.Font.FontSize = 11;

        // Mağaza bazında grupla (aynı mağaza için birden fazla icmal olabilir)
        var formsByStore = claim.ServiceForms
            .Where(f => f.Store != null)
            .GroupBy(f => f.StoreId)
            .ToList();

        var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storeSheetData = new List<(string SheetName, string StoreName, string StoreCode, decimal Total, List<int> KdvTotalRows, int StoreTotalRow)>();

        foreach (var storeGroup in formsByStore)
        {
            var storeCode = storeGroup.First().Store!.Code;
            var storeName = storeGroup.First().Store!.Name;
            var forms = storeGroup.ToList();

            progress?.Report($"Mağaza {storeCode} işleniyor...");

            var sheetName = CreateSafeWorksheetName(storeName, usedSheetNames);

            var ws = wb.Worksheets.Add(sheetName);

            decimal storeTotal = forms.Sum(f =>
                f.ServiceItems.Sum(i => i.TotalPrice) +
                f.Invoices.Sum(i => i.CalculatedTotal));
            // Hem icmal (A-J) hem de ek belge görselleri (K+) aynı sayfada
            var (kdvTotalRows, storeTotalRow) = BuildIcmalSheet(ws, claim, storeCode, storeName, companyName, forms, claim.ClaimType, posExportLog);
            storeSheetData.Add((sheetName, storeName, storeCode, storeTotal, kdvTotalRows, storeTotalRow));
            progress?.Report($"Mağaza {storeCode} ek belgeler ekleniyor...");
            await AddDocumentImagesAsync(ws, storeCode, storeName, forms, docQuality);
        }

        // İcmal özet sayfasını en başa ekle (her zaman)
        progress?.Report("İcmal özet sayfası oluşturuluyor...");
        const string icmalSheetName = "İcmal";
        var icmalWs = wb.Worksheets.Add(icmalSheetName);
        icmalWs.Position = 1;
        BuildSummarySheet(icmalWs, claim, companyName, storeSheetData);

        // Formsuz icmaller (mağaza seçilmemiş)
        var unstoredForms = claim.ServiceForms.Where(f => f.Store is null).ToList();
        if (unstoredForms.Any())
        {
            progress?.Report("Mağazasız formlar ekleniyor...");
            var wsUnsorted = wb.Worksheets.Add("Atanmamış Formlar");
            BuildUnsortedSheet(wsUnsorted, unstoredForms);
        }

        progress?.Report("Excel dosyası kaydediliyor...");
        var fileName = $"Hakedis_{claim.Year}_{claim.Month:D2}_{SanitizeSheetName(claim.Name)}_{DateTime.Now:HHmmss}.xlsx";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        // Eski aktif exportları arşive taşı
        await _cleanup.MoveOldExportsToArchiveAsync(claimId, claim.Year, claim.Month);

        var filePath = await _fileStorage.SaveExportAsync(ms, fileName, claim.Year, claim.Month);
        var absoluteFilePath = _fileStorage.GetAbsolutePath(filePath);

        // Yangın\Exports klasörüne ek (mirror) kopya — canonical kayıt yolu (yukarıdaki
        // filePath) değişmez; backup/restore ve dosya geçmişi bu yola bağlı kalmaya devam eder.
        MirrorExportToDisciplineFolder(absoluteFilePath, fileName);
        WriteDisciplinePathLog(absoluteFilePath);

        // Export log kaydet
        _db.ExportLogs.Add(new ExportLog
        {
            ProgressClaimId = claimId,
            FilePath = filePath,
            ExportType = ExportType.Excel,
            ExportedAt = DateTime.Now,
            IsActive = true
        });
        claim.Status = ProgressClaimStatus.DisaAktarildi;
        await _db.SaveChangesAsync();

        progress?.Report("Tamamlandı!");

        // POS export log yaz (ilk 10 kalem + dosya bilgisi) — sadece Yangın için anlamlı
        if (posEnabled)
            _posMapping.WriteExportLog(posExportLog, claim.Name ?? "?");

        // Mutlak yol döndür — çağıran taraf File.Exists / Process.Start için kullanır
        return _fileStorage.GetAbsolutePath(filePath);
    }

    /// <summary>
    /// Excel çıktısını Yangın\Exports klasörüne de kopyalar (sadece Fire disiplini için).
    /// Bu, kullanıcının ayarlardaki "Yangın Export Klasörünü Aç" butonunda dosyayı görmesini
    /// sağlar — canonical (DB'de kayıtlı) yol değişmez, mevcut yedekleme/arşiv sistemi bozulmaz.
    /// </summary>
    private void MirrorExportToDisciplineFolder(string absoluteFilePath, string fileName)
    {
        try
        {
            var targetDir = _disciplineProfiles.GetExportsPath(_currentDiscipline.CurrentDiscipline);
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, fileName);
            File.Copy(absoluteFilePath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Export Yangın klasörüne kopyalanamadı.");
        }
    }

    /// <summary>
    /// Hata ayıklama için aktif disiplinin tüm yollarını ve POS/export durumunu loglar.
    /// Common\Logs\discipline-path-log.txt dosyasına eklenir.
    /// </summary>
    private void WriteDisciplinePathLog(string exportedFilePath)
    {
        try
        {
            var discipline = _currentDiscipline.CurrentDiscipline;
            var logDir = _disciplineProfiles.GetCommonPath() is { } common ? Path.Combine(common, "Logs") : null;
            if (logDir is null) return;
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "discipline-path-log.txt");

            var posFileFound = File.Exists(_posMapping.PosFilePath);
            var lines = new[]
            {
                $"=== Export — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"Aktif Disiplin: {discipline}",
                $"MasterData: {_disciplineProfiles.GetMasterDataPath(discipline)}",
                $"POS Dosyası: {_posMapping.PosFilePath} (bulundu: {posFileFound})",
                $"ReferenceTables: {_disciplineProfiles.GetReferenceTablesPath(discipline)}",
                $"Uploads: {_disciplineProfiles.GetUploadsPath(discipline)}",
                $"Exports: {_disciplineProfiles.GetExportsPath(discipline)}",
                $"Export Çıktısı: {exportedFilePath}",
                string.Empty
            };
            File.AppendAllLines(logFile, lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disiplin path log yazılamadı.");
        }
    }

    private static string GetIcmalTitle(ProgressClaimType claimType) => claimType switch
    {
        ProgressClaimType.NewConstruction => "HİZMET HAKEDİŞ İCMALİ - YENİ YAPIM",
        ProgressClaimType.Renovation      => "HİZMET HAKEDİŞ İCMALİ - TADİLAT",
        _                                 => "HİZMET HAKEDİŞ İCMALİ"
    };

    private static string GetFormSubHeaderLabel(ProgressClaimType claimType, bool hasFile, int icmalNo) =>
        claimType switch
        {
            ProgressClaimType.NewConstruction => "İcmal Tablosu — Yeni Yapım",
            ProgressClaimType.Renovation      => "İcmal Tablosu — Tadilat",
            _ when !hasFile                   => "İcmal Tablosu — Bakım / Onarım",
            _                                 => $"İcmal Tablosu — Servis Formu {icmalNo}"
        };

    private (List<int> KdvTotalRows, int StoreTotalRow) BuildIcmalSheet(
        IXLWorksheet ws,
        ProgressClaim claim, string storeCode, string storeName,
        string companyName, List<ServiceForm> forms,
        ProgressClaimType claimType = ProgressClaimType.MaintenanceRepair,
        List<string>? exportLog = null)
    {
        // ─── Sayfa yapılandırması ───────────────────────────────────────────
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.SheetView.ZoomScale = 100;

        // Sütun genişlikleri (A-J icmal, K+ ek belgeler)
        ws.Column(1).Width = 10;  // POS
        ws.Column(2).Width = 85;  // Açıklama
        ws.Column(3).Width = 10;  // Miktar
        ws.Column(4).Width = 8;   // Birim
        ws.Column(5).Width = 20;  // Malzeme
        ws.Column(6).Width = 20;  // İşçilik
        ws.Column(7).Width = 20;  // Birim Fiyat
        ws.Column(8).Width = 22;  // Toplam Fiyat
        ws.Column(9).Width = 4;   // Boşluk (I)
        ws.Column(10).Width = 4;  // Boşluk (J)
        // K+ sütunları ek belgeler için (genişlik AddDocumentImagesAsync'te ayarlanır)

        // ← İcmale Dön — en üst kısımda küçük hyperlink
        ws.Range(1, 1, 1, 2).Merge();
        var backLinkCell = ws.Cell(1, 1);
        backLinkCell.FormulaA1 = "=HYPERLINK(\"#'İcmal'!A1\",\"← İcmale Dön\")";
        backLinkCell.Style.Font.FontColor = XLColor.FromArgb(0, 70, 173);
        backLinkCell.Style.Font.Underline = XLFontUnderlineValues.Single;
        backLinkCell.Style.Font.FontSize = 9;
        ws.Row(1).Height = 14;

        int row = 2;

        // ─── Başlık bloğu ──────────────────────────────────────────────────
        var headerRange = ws.Range(row, 1, row, 8);
        headerRange.Merge();
        headerRange.Value = companyName.ToUpper();
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontSize = 14;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromArgb(21, 96, 189);
        headerRange.Style.Font.FontColor = XLColor.White;
        ApplyThinBorder(headerRange);
        row++;

        var titleRange = ws.Range(row, 1, row, 8);
        titleRange.Merge();
        titleRange.Value = GetIcmalTitle(claimType);
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 12;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        titleRange.Style.Fill.BackgroundColor = XLColor.FromArgb(52, 152, 219);
        titleRange.Style.Font.FontColor = XLColor.White;
        ApplyThinBorder(titleRange);
        row++;

        // Mağaza bilgi satırı
        ws.Cell(row, 1).Value = "Mağaza Kodu:";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).Value = storeCode;
        ws.Cell(row, 3).Value = "Mağaza Adı:";
        ws.Cell(row, 3).Style.Font.Bold = true;
        ws.Range(row, 4, row, 6).Merge().Value = storeName;
        ws.Cell(row, 7).Value = "Tarih:";
        ws.Cell(row, 7).Style.Font.Bold = true;
        ws.Cell(row, 8).Value = new DateTime(claim.Year, claim.Month, DateTime.DaysInMonth(claim.Year, claim.Month)).ToString("dd.MM.yyyy dddd", TrCulture);
        ApplyThinBorder(ws.Range(row, 1, row, 8));
        row++;

        // Firma satırı
        ws.Cell(row, 1).Value = "Firma:";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Range(row, 2, row, 8).Merge().Value = claim.CompanyName;
        ApplyThinBorder(ws.Range(row, 1, row, 8));
        row++;

        row++; // boşluk

        // ─── Her servis formu için ayrı icmal tablosu ──────────────────────
        var icmalHeaders = new[] { "POS", "Açıklama", "Miktar", "Birim", "Malzeme", "İşçilik", "Birim Fiyat", "Toplam Fiyat" };
        int icmalNo = 1;
        var kdvTotalRows = new List<int>(); // KDV toplam satır numaraları (İcmal formülü için)

        foreach (var form in forms)
        {
            // ── İcmal alt başlığı ──────────────────────────────────────────
            var icmalSubHeader = ws.Range(row, 1, row, 8);
            icmalSubHeader.Merge();
            icmalSubHeader.Value = GetFormSubHeaderLabel(claimType, !string.IsNullOrEmpty(form.FilePath), icmalNo);
            icmalSubHeader.Style.Font.Bold = true;
            icmalSubHeader.Style.Font.FontSize = 11;
            icmalSubHeader.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            icmalSubHeader.Style.Fill.BackgroundColor = XLColor.FromArgb(44, 62, 80);
            icmalSubHeader.Style.Font.FontColor = XLColor.White;
            ApplyThinBorder(icmalSubHeader);
            row++;

            // ── Tablo başlığı ──────────────────────────────────────────────
            for (int c = 0; c < icmalHeaders.Length; c++)
            {
                var cell = ws.Cell(row, c + 1);
                cell.Value = icmalHeaders[c];
                cell.Style.Font.Bold = true;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.WrapText = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(52, 73, 94);
                cell.Style.Font.FontColor = XLColor.White;
            }
            ApplyThinBorder(ws.Range(row, 1, row, 8));
            row++;

            // ── Kalem satırları ────────────────────────────────────────────
            int itemNo = 1;
            decimal formTotal = 0;
            int firstDataRow = row;

            // Formül referansları için satır takibi
            var parentKeyToExcelRow = new Dictionary<string, int>();
            var bp30Rows      = new List<int>();   // siyah boru 1"–2" grubu
            var bp80Rows      = new List<int>();   // siyah boru 2½"–5" grubu
            var galvSteelRows = new List<int>();   // dikişli galvanizli çelik boru
            var galvDuctRows  = new List<int>();   // galvaniz sac dikdörtgen hava kanalı (0.5/0.6/0.8/1.0 mm)
            var paintSrcSmall  = new List<int>();  // sülyen boya küçük grup kaynak satırları (1/2"–1½")
            var paintSrcMedium = new List<int>();  // sülyen boya orta grup kaynak satırları (2"–4")
            var paintSrcLarge  = new List<int>();  // sülyen boya büyük grup kaynak satırları (4"–10")
            int paintSulyenSmallRow  = 0; // Excel satırı — sülyen küçük
            int paintSulyenMediumRow = 0; // Excel satırı — sülyen orta
            int paintSulyenLargeRow  = 0; // Excel satırı — sülyen büyük

            // --- Servis kalemleri gruplama ---
            var orderedItems = form.ServiceItems.OrderBy(i => i.SortOrder).ToList();
            var seenItemIds  = new HashSet<int>();

            var serviceFeeList = orderedItems.Where(ExportIsServiceFee).ToList();
            foreach (var si in serviceFeeList) seenItemIds.Add(si.Id);

            var fireList  = new List<ServiceItem>();
            var hvacList  = new List<ServiceItem>();
            var otherList = new List<ServiceItem>();

            var sectionRules = _calculationRuleFactory.GetRules(_currentDiscipline.CurrentDiscipline);
            foreach (var si in orderedItems.Where(i => !seenItemIds.Contains(i.Id)))
            {
                seenItemIds.Add(si.Id);
                var section = sectionRules.ResolveSectionName(si.Description, si.AutoGeneratedType, si.IsInvoiceBased);
                switch (section)
                {
                    case "DİĞER":        otherList.Add(si); break;
                    case "HAVALANDIRMA": hvacList.Add(si);  break;
                    case "YANGIN":       fireList.Add(si);  break;
                    default:             fireList.Add(si);  break; // disiplin ilgilenmiyorsa varsayılan davranış
                }
            }

            // --- Kalem satırı yazma yerel fonksiyonu ---
            void WriteServiceItemRow(ServiceItem item)
            {
                bool isEven = (itemNo % 2 == 0);
                var rowColor = isEven ? XLColor.FromArgb(235, 241, 248) : XLColor.White;

                // Grup formülü için satır takibi
                if (!item.IsAutoGenerated && !string.IsNullOrEmpty(item.ItemKey))
                    parentKeyToExcelRow[item.ItemKey] = row;
                if (!item.IsAutoGenerated)
                {
                    if (item.AutoRuleTrigger == "SiyahBoruGroup30")
                        bp30Rows.Add(row);
                    else if (item.AutoRuleTrigger == "SiyahBoruGroup80")
                        bp80Rows.Add(row);
                    else if (item.AutoRuleTrigger == "DikisliGalvanizliCelikBoru" ||
                             IsExportGalvSteelPipe(item.Description))
                        galvSteelRows.Add(row);
                    // Galvaniz hava kanalı: "GalvanizedDuct" (yeni), "Fittings" (eski uyumluluk)
                    // veya tetikleyici kaydedilmemişse açıklama bazlı tespit
                    if (item.AutoRuleTrigger == "GalvanizedDuct" ||
                        item.AutoRuleTrigger == "Fittings" ||
                        (item.AutoRuleTrigger == null && IsExportGalvanizedDuct(item.Description)))
                        galvDuctRows.Add(row);

                    // Sülyen boya kaynak satır takibi — DİKİŞLİ SİYAH ÇELİK BORU
                    if (IsExportBlackSteelPipe(item.Description))
                    {
                        var pd = ExportExtractPipeDiameter(ExportNrmPipe(item.Description));
                        if (pd.HasValue)
                        {
                            if (pd.Value >= 0.5m && pd.Value <= 1.5m) paintSrcSmall.Add(row);
                            else if (pd.Value >= 2m  && pd.Value <= 4m)  paintSrcMedium.Add(row);
                            else if (pd.Value >  4m  && pd.Value <= 10m) paintSrcLarge.Add(row);
                        }
                    }
                }

                // POS sistemi sadece Yangın'a aittir — diğer disiplinlerde kolon boş bırakılır,
                // Yangın POS dosyasına asla fallback yapılmaz.
                ws.Cell(row, 1).Value = _currentDiscipline.CurrentDiscipline == MechanicalDiscipline.Fire
                    ? _posMapping.ResolvePosForExport(item.Description, item.AutoGeneratedType, exportLog is { Count: < 10 } ? exportLog : null)
                    : string.Empty;
                ws.Cell(row, 2).Value = BuildItemExcelDescription(item);
                bool isRedOxidePaintRow = item.IsAutoGenerated && item.AutoGeneratedType == "BlackSteelPipeRedOxidePaint";
                bool isPolisanPaintRow  = item.IsAutoGenerated && item.AutoGeneratedType == "BlackSteelPipePolisanPaint";
                bool isPaintRow = isRedOxidePaintRow || isPolisanPaintRow;
                if (item.IsAutoGenerated && !isPaintRow)
                {
                    ws.Cell(row, 3).Value = $"{(int)item.Quantity}%";
                }
                else if (isRedOxidePaintRow)
                {
                    var descNorm = ExportNrm(item.Description);
                    List<int> srcPaintRows;
                    if (descNorm.Contains("3/4"))     srcPaintRows = paintSrcSmall;
                    else if (descNorm.Contains("10")) srcPaintRows = paintSrcLarge;
                    else                              srcPaintRows = paintSrcMedium;

                    if (srcPaintRows.Count > 0)
                        ws.Cell(row, 3).FormulaA1 = string.Join("+", srcPaintRows.Select(r => $"C{r}"));
                    else
                        ws.Cell(row, 3).Value = item.Quantity;

                    if (descNorm.Contains("3/4"))      paintSulyenSmallRow  = row;
                    else if (descNorm.Contains("10"))  paintSulyenLargeRow  = row;
                    else                               paintSulyenMediumRow = row;
                }
                else if (isPolisanPaintRow)
                {
                    var descNorm = ExportNrm(item.Description);
                    int sulyenRef;
                    if (descNorm.Contains("3/4"))     sulyenRef = paintSulyenSmallRow;
                    else if (descNorm.Contains("10")) sulyenRef = paintSulyenLargeRow;
                    else                              sulyenRef = paintSulyenMediumRow;

                    if (sulyenRef > 0)
                        ws.Cell(row, 3).FormulaA1 = $"=C{sulyenRef}";
                    else
                        ws.Cell(row, 3).Value = item.Quantity;
                }
                else
                {
                    ws.Cell(row, 3).Value = item.Quantity;
                }
                ws.Cell(row, 4).Value = item.Unit;
                ws.Cell(row, 5).Value = item.MaterialPrice;
                ws.Cell(row, 6).Value = item.LaborPrice;
                if (isPaintRow)
                {
                    ws.Cell(row, 7).FormulaA1 = $"=E{row}+F{row}";
                    ws.Cell(row, 8).FormulaA1 = $"=C{row}*G{row}";
                }
                else if (item.IsAutoGenerated)
                {
                    ws.Cell(row, 7).Value = 0m;
                    var fittingFormula = BuildAutoFittingFormula(
                        item, parentKeyToExcelRow, bp30Rows, bp80Rows, galvSteelRows, galvDuctRows);
                    if (fittingFormula != null)
                        ws.Cell(row, 8).FormulaA1 = fittingFormula;
                    else
                        ws.Cell(row, 8).Value = item.TotalPrice;
                }
                else
                {
                    ws.Cell(row, 7).FormulaA1 = $"=E{row}+F{row}";
                    ws.Cell(row, 8).FormulaA1 = $"=C{row}*G{row}";
                }

                foreach (int col in new[] { 5, 6, 7, 8 })
                    ws.Cell(row, col).Style.NumberFormat.Format = MoneyFormat;
                if (item.IsAutoGenerated && !isPaintRow)
                    ws.Cell(row, 3).Style.NumberFormat.Format = "@";
                else
                    ws.Cell(row, 3).Style.NumberFormat.NumberFormatId = 0;
                ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                var dataRow = ws.Range(row, 1, row, 8);
                dataRow.Style.Fill.BackgroundColor = rowColor;
                ApplyThinBorder(dataRow);

                ws.Cell(row, 2).Style.Alignment.WrapText = false;
                ws.Cell(row, 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                if (item.IsInvoiceBased)
                {
                    ws.Cell(row, 2).Style.Font.Italic = true;
                    ws.Cell(row, 2).Style.Font.FontColor = XLColor.FromArgb(52, 152, 219);
                }

                ApplyDescriptionWrapAndHeight(ws, row);
                formTotal += item.TotalPrice;
                itemNo++;
                row++;
            }

            void WriteGroupHeader(string title)
            {
                var headerRange = ws.Range(row, 1, row, 8);
                headerRange.Merge().Value = title;
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontSize = 10;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromArgb(44, 62, 80);
                headerRange.Style.Font.FontColor = XLColor.White;
                ApplyThinBorder(headerRange);
                row++;
            }

            // --- Servis ücretleri (başlık yok) ---
            foreach (var item in serviceFeeList) WriteServiceItemRow(item);

            // --- Yangın tesisatı ---
            if (fireList.Any())
            {
                WriteGroupHeader("YANGIN TESİSATI");
                foreach (var item in fireList) WriteServiceItemRow(item);
            }

            // --- Havalandırma tesisatı ---
            if (hvacList.Any())
            {
                WriteGroupHeader("HAVALANDIRMA TESİSATI");
                foreach (var item in hvacList) WriteServiceItemRow(item);
            }

            // --- Diğer kalemler (fatura kaynaklı ServiceItem'lar + Invoice tablosu) ---
            if (otherList.Any() || form.Invoices.Any())
                WriteGroupHeader("DİĞER KALEMLER");
            foreach (var item in otherList) WriteServiceItemRow(item);

            // --- Fatura +%10 satırları (Invoice tablosundan, ServiceItem olmaksızın) ---
            foreach (var invoice in form.Invoices.OrderBy(i => i.UploadedAt))
            {
                bool isEven = (itemNo % 2 == 0);
                var rowColor = isEven ? XLColor.FromArgb(255, 243, 205) : XLColor.FromArgb(255, 249, 229);

                var unitPrice = invoice.InvoiceAmount * (1 + invoice.MarkupRate);

                ws.Cell(row, 1).Value = string.Empty;
                ws.Cell(row, 2).Value = invoice.Description + " (Fatura +%10)";
                ws.Cell(row, 3).Value = invoice.Quantity;
                ws.Cell(row, 4).Value = "Ad";
                ws.Cell(row, 5).Value = unitPrice;            // Malzeme = fatura+markup birim fiyat
                ws.Cell(row, 6).Value = 0m;                   // İşçilik = 0
                ws.Cell(row, 7).FormulaA1 = $"=E{row}+F{row}";
                ws.Cell(row, 8).FormulaA1 = $"=C{row}*G{row}";

                foreach (int col in new[] { 5, 6, 7, 8 })
                    ws.Cell(row, col).Style.NumberFormat.Format = MoneyFormat;
                ws.Cell(row, 3).Style.NumberFormat.NumberFormatId = 0; // General
                ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                var dataRow = ws.Range(row, 1, row, 8);
                dataRow.Style.Fill.BackgroundColor = rowColor;
                ApplyThinBorder(dataRow);

                ws.Cell(row, 2).Style.Font.Italic = true;
                ws.Cell(row, 2).Style.Font.FontColor = XLColor.FromArgb(180, 90, 0);

                ApplyDescriptionWrapAndHeight(ws, row);

                formTotal += invoice.CalculatedTotal;
                itemNo++;
                row++;
            }

            // ── Bu form için KDV hariç toplam ──────────────────────────────
            ws.Range(row, 1, row, 7).Merge().Value = "KDV HARİÇ GENEL TOPLAM";
            ws.Range(row, 1, row, 7).Style.Font.Bold = true;
            ws.Range(row, 1, row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Range(row, 1, row, 7).Style.Fill.BackgroundColor = XLColor.FromArgb(44, 62, 80);
            ws.Range(row, 1, row, 7).Style.Font.FontColor = XLColor.White;

            int lastDataRow = row - 1;
            if (lastDataRow >= firstDataRow)
                ws.Cell(row, 8).FormulaA1 = $"=SUM(H{firstDataRow}:H{lastDataRow})";
            else
                ws.Cell(row, 8).Value = formTotal;
            ws.Cell(row, 8).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 8).Style.Font.Bold = true;
            ws.Cell(row, 8).Style.Fill.BackgroundColor = XLColor.FromArgb(52, 152, 219);
            ws.Cell(row, 8).Style.Font.FontColor = XLColor.White;
            ApplyThinBorder(ws.Range(row, 1, row, 8));
            kdvTotalRows.Add(row); // KDV genel toplam hücresi (İcmal sayfası formülü için)
            row++;

            row += 2; // İcmal tabloları arası boşluk
            icmalNo++;
        }

        // ─── Çoklu form varsa Mağaza Toplam Hakediş Tutarı satırı ─────────
        int storeTotalRow;
        if (kdvTotalRows.Count > 1)
        {
            row++; // boşluk
            var magTotalRange = ws.Range(row, 1, row, 7);
            magTotalRange.Merge().Value = "MAĞAZA TOPLAM HAKEDİŞ TUTARI";
            magTotalRange.Style.Font.Bold = true;
            magTotalRange.Style.Font.FontSize = 11;
            magTotalRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            magTotalRange.Style.Fill.BackgroundColor = XLColor.FromArgb(22, 46, 80);
            magTotalRange.Style.Font.FontColor = XLColor.White;
            ws.Cell(row, 8).FormulaA1 = string.Join("+", kdvTotalRows.Select(r => $"H{r}"));
            ws.Cell(row, 8).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 8).Style.Font.Bold = true;
            ws.Cell(row, 8).Style.Fill.BackgroundColor = XLColor.FromArgb(21, 96, 189);
            ws.Cell(row, 8).Style.Font.FontColor = XLColor.White;
            ApplyThinBorder(ws.Range(row, 1, row, 8));
            storeTotalRow = row;
            row += 2;
        }
        else
        {
            // Tek form: zaten kdvTotalRows[0] yeterli
            storeTotalRow = kdvTotalRows.Count > 0 ? kdvTotalRows[0] : -1;
        }

        // ─── Not alanı ─────────────────────────────────────────────────────
        ws.Cell(row, 1).Value = "Not: Bu icmal otomatik olarak oluşturulmuştur.";
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = XLColor.Gray;
        ws.Range(row, 1, row, 8).Merge();
        return (kdvTotalRows, storeTotalRow);
    }

    /// <summary>
    /// K sütunundan (kolon 11) itibaren servis formu ve fatura görsellerini
    /// yatay belge blokları halinde yan yana yerleştirir.
    /// — EK BELGELER başlığı yalnızca kullanılan kolon aralığında merge edilir.
    /// — Her görsel kendi bloğunun piksel genişliğine ölçeklenir (en-boy oranı korunur).
    /// </summary>
    private async Task AddDocumentImagesAsync(
        IXLWorksheet ws,
        string storeCode, string storeName,
        List<ServiceForm> forms,
        ImageQualityOptions? docQuality = null)
    {
        docQuality ??= ImageOptimizer.Preset.Standard;
        const int startCol      = 11;   // K sütunu — belge alanı buradan başlar
        const int blockWidth    = 6;    // Normal belge bloğu genişliği (sütun)
        const int tblBlockWidth = 8;    // Teknik tablo bloğu genişliği (sütun)
        const int gapWidth      = 1;    // Bloklar arası boşluk
        const int docStartRow   = 3;    // EK BELGELER başlığından 2 satır sonra
        const double colCharW   = 14.0; // Her sütuna atanan genişlik (karakter birimi)

        // ── Yardımcılar ───────────────────────────────────────────────────
        int BlockStart(int i) => startCol + i * (blockWidth + gapWidth);
        int BlockEnd(int i)   => BlockStart(i) + blockWidth - 1;

        // ── Belge listesi ─────────────────────────────────────────────────
        var docs = new List<(string Title, string? FilePath, string FileName,
                              string Date, string? InvoiceDesc, string Total,
                              bool IsInvoice, int IcmalNo)>();
        int docNo    = 1;
        int icmalIdx = 1;

        foreach (var form in forms)
        {
            decimal formTotal = form.ServiceItems.Sum(i => i.TotalPrice)
                              + form.Invoices.Sum(i => i.CalculatedTotal);

            // Dosyası olan formlar Ek Belgeler bölümünde servis formu olarak gösterilir
            if (!string.IsNullOrEmpty(form.FilePath))
            {
                docs.Add((
                    $"Belge #{docNo} — Servis Formu",
                    form.FilePath,
                    form.OriginalFileName,
                    form.UploadedAt.ToString("dd.MM.yyyy"),
                    null,
                    formTotal.ToString("N2", TrCulture) + " TL",
                    false,
                    icmalIdx
                ));
                docNo++;
            }

            // Faturalar: servis formu dosyasından bağımsız olarak eklenir.
            // Hakediş türü ne olursa olsun (Yeni Yapım, Tadilat, Bakım/Onarım),
            // fatura yüklendiyse Excel'de FATURALAR başlığı altında görünür.
            foreach (var invoice in form.Invoices.OrderBy(i => i.UploadedAt))
            {
                docs.Add((
                    $"Belge #{docNo} — Fatura",
                    invoice.FilePath,
                    invoice.FileName ?? invoice.Description,
                    invoice.UploadedAt.ToString("dd.MM.yyyy"),
                    invoice.Description,
                    invoice.CalculatedTotal.ToString("N2", TrCulture) + " TL",
                    true,
                    icmalIdx
                ));
                docNo++;
            }

            icmalIdx++;
        }

        // ── Teknik referans tablo algılama ────────────────────────────────
        var allItems = forms.SelectMany(f => f.ServiceItems).ToList();
        var refTableRules = _calculationRuleFactory.GetRules(_currentDiscipline.CurrentDiscipline);
        bool needsFlexiva = allItems.Any(i => refTableRules.ShouldIncludeReferenceTable(i.Description, "FLEXIVA"));
        bool needsBdkf    = allItems.Any(i => refTableRules.ShouldIncludeReferenceTable(i.Description, "BDKF"));
        bool needsBdtx    = allItems.Any(i => refTableRules.ShouldIncludeReferenceTable(i.Description, "BDTX"));

        var refTables = new List<(string Title, string FileNameHint)>();
        if (needsFlexiva) refTables.Add(("FLEXIVA AIR FİYAT TABLOSU",             "flexiva-table.png"));
        if (needsBdkf)    refTables.Add(("BDKF BVN KANAL TİPİ FAN FİYAT TABLOSU", "bdkf-table.png"));
        if (needsBdtx)    refTables.Add(("BDTX BVN KANAL TİPİ FAN FİYAT TABLOSU", "bdtx-table.png"));

        if (docs.Count == 0 && refTables.Count == 0) return;

        // ── Kolon hesabı ─────────────────────────────────────────────────
        // Teknik tablo blokları, normal belge bloklarının hemen sağından başlar
        int tblBaseCol = startCol + docs.Count * (blockWidth + gapWidth);
        int TblBlockStart(int j) => tblBaseCol + j * (tblBlockWidth + gapWidth);
        int TblBlockEnd(int j)   => TblBlockStart(j) + tblBlockWidth - 1;

        int docLastCol     = docs.Count    > 0 ? BlockEnd(docs.Count - 1)        : startCol - 1;
        int tblLastCol     = refTables.Count > 0 ? TblBlockEnd(refTables.Count - 1) : tblBaseCol - 1;
        int overallLastCol = refTables.Count > 0 ? tblLastCol : docLastCol;

        // ── Sütun genişlikleri ────────────────────────────────────────────
        for (int c = startCol; c <= overallLastCol; c++)
            ws.Column(c).Width = colCharW;

        // ── EK BELGELER başlığı — kullanılan aralıkta merge ──────────────
        var titleRange = ws.Range(1, startCol, 1, overallLastCol);
        titleRange.Merge().Value = "EK BELGELER";
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 12;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        titleRange.Style.Fill.BackgroundColor = XLColor.FromArgb(44, 62, 80);
        titleRange.Style.Font.FontColor = XLColor.White;
        ApplyThinBorder(titleRange);

        // ── Normal belge blokları ─────────────────────────────────────────
        for (int i = 0; i < docs.Count; i++)
        {
            var doc    = docs[i];
            int bStart = BlockStart(i);
            int bEnd   = BlockEnd(i);
            int row    = docStartRow;

            var hdr = ws.Range(row, bStart, row, bEnd);
            hdr.Merge().Value = doc.Title;
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = doc.IsInvoice
                ? XLColor.FromArgb(230, 126, 34)
                : XLColor.FromArgb(52, 152, 219);
            hdr.Style.Font.FontColor = XLColor.White;
            ApplyThinBorder(hdr);
            row++;

            AddInfoRowCard(ws, ref row, bStart, blockWidth, "Dosya", doc.FileName);
            AddInfoRowCard(ws, ref row, bStart, blockWidth, "Tarih", doc.Date);
            if (doc.IsInvoice && doc.InvoiceDesc is not null)
                AddInfoRowCard(ws, ref row, bStart, blockWidth, "Açıklama", doc.InvoiceDesc);
            AddInfoRowCard(ws, ref row, bStart, blockWidth, "Tutar", doc.Total);
            AddInfoRowCard(ws, ref row, bStart, blockWidth, "İcmal", doc.IcmalNo.ToString());

            if (doc.FilePath is not null)
                await InsertFittedImageAsync(ws, doc.FilePath, row, bStart, bEnd, docQuality);
        }

        // ── Teknik fiyat tablosu blokları ─────────────────────────────────
        for (int j = 0; j < refTables.Count; j++)
        {
            var (title, fileNameHint) = refTables[j];
            AddReferenceTableImage(ws, title, fileNameHint, docStartRow, TblBlockStart(j));
        }
    }

    /// <summary>
    /// Görseli belirtilen hücreye yerleştirir.
    /// Genişlik her zaman startCol–endCol bloğunun gerçek piksel genişliğine eşitlenir;
    /// yükseklik orijinal en-boy oranından türetilir (kırpma/kısıt yok).
    /// </summary>
    private async Task InsertFittedImageAsync(
        IXLWorksheet ws, string filePath,
        int startRow, int startCol, int endCol,
        ImageQualityOptions? quality = null)
    {
        quality ??= ImageOptimizer.Preset.Standard;
        try
        {
            var absPath = _fileStorage.GetAbsolutePath(filePath);
            byte[]? imageBytes = null;

            if (_pdfPreview.IsImage(filePath))
                imageBytes = await File.ReadAllBytesAsync(absPath);
            else if (_pdfPreview.IsPdf(filePath))
                imageBytes = await _pdfPreview.RenderFirstPageAsync(absPath, quality.MaxWidthPx);

            if (imageBytes is null) return;

            // Görsel optimizasyonu (yeniden boyutlandırma + JPEG sıkıştırma)
            imageBytes = await Task.Run(() => ImageOptimizer.Optimize(imageBytes, quality));

            using var imgStream = new MemoryStream(imageBytes);
            var picture = ws.AddPicture(imgStream);

            int origW = picture.OriginalWidth;
            int origH = picture.OriginalHeight;

            if (origW <= 0 || origH <= 0) { picture.Delete(); return; }

            // Genişlik = bloğun tam piksel genişliği (yüksekliği asla kısıtlamıyoruz)
            int blockPx     = GetBlockPixelWidth(ws, startCol, endCol);
            double aspect   = (double)origW / origH;
            int targetWidth = blockPx;
            int targetHeight = (int)Math.Round(targetWidth / aspect);

            picture.MoveTo(ws.Cell(startRow, startCol));
            picture.Width  = targetWidth;
            picture.Height = targetHeight;

            // Görsel kapladığı satırların yüksekliğini ayarla.
            // rowPts = 15.75 (Calibri 11pt standardı) — veri satırlarını gereksiz
            // yükseltmemek için 20pt yerine standart yükseklik kullanılır.
            // Math.Max ile önceden belirlenmiş daha büyük bir yükseklik korunur.
            const double rowPts  = 15.75;
            const double pxPerPt = 96.0 / 72.0;
            int numRows = (int)Math.Ceiling(targetHeight / (rowPts * pxPerPt)) + 1;
            for (int r = startRow; r < startRow + numRows; r++)
                ws.Row(r).Height = Math.Max(ws.Row(r).Height, rowPts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Görsel eklenemedi: {Path}", filePath);
        }
    }

    /// <summary>
    /// Gömülü kaynak (EmbeddedResource) akışını MemoryStream olarak döndürür.
    /// Görseller Web assembly'e gömülüdür; Infrastructure'dan erişmek için
    /// tüm yüklü assembly'ler taranır — fiziksel dosya yolu kullanılmaz.
    /// </summary>
    /// <summary>
    /// BDKF/BDTX/Flexiva referans tablo görselini önce aktif disiplinin ReferenceTables
    /// klasöründen (kullanıcı kendi tablosuyla değiştirmiş olabilir) okur. Bulunamazsa,
    /// SADECE Yangın için uygulamaya gömülü kaynağa düşer — bu görseller Yangın'a özel
    /// olduğundan başka bir disiplinin tablosuna yanlışlıkla sızdırılmaz.
    /// </summary>
    private Stream? GetReferenceTableImageStream(string fileName)
    {
        var discipline = _currentDiscipline.CurrentDiscipline;
        try
        {
            var diskPath = Path.Combine(_disciplineProfiles.GetReferenceTablesPath(discipline), fileName);
            if (File.Exists(diskPath))
            {
                var bytes = File.ReadAllBytes(diskPath);
                return new MemoryStream(bytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk referans tablosu okunamadı: {File}", fileName);
        }

        if (discipline != MechanicalDiscipline.Fire) return null;
        return GetEmbeddedResourceStream(fileName);
    }

    private static Stream? GetEmbeddedResourceStream(string fileName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null) continue;

            var sourceStream = asm.GetManifestResourceStream(resourceName);
            if (sourceStream is null) continue;

            var memoryStream = new MemoryStream();
            sourceStream.CopyTo(memoryStream);
            memoryStream.Position = 0;
            return memoryStream;
        }
        return null;
    }

    /// <summary>
    /// Gömülü teknik fiyat tablosu görselini başlığıyla birlikte çalışma sayfasına ekler.
    /// Kaynak bulunamazsa hata loglanır, Excel oluşturma durmaz.
    /// </summary>
    private void AddReferenceTableImage(
        IXLWorksheet ws,
        string title,
        string fileName,
        int startRow,
        int startCol)
    {
        // Başlık hücresi
        var hdrRange = ws.Range(startRow, startCol, startRow, startCol + 7);
        hdrRange.Merge();
        var hdrCell = ws.Cell(startRow, startCol);
        hdrCell.Value                      = title;
        hdrCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#102A42");
        hdrCell.Style.Font.FontColor       = XLColor.White;
        hdrCell.Style.Font.Bold            = true;
        hdrCell.Style.Font.FontSize        = 10;
        hdrCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ApplyThinBorder(hdrRange);

        using var stream = GetReferenceTableImageStream(fileName);

        if (stream is null)
        {
            _logger.LogWarning("Referans tablo görseli bulunamadı: {File}", fileName);
            ws.Cell(startRow + 1, startCol).Value = $"Görsel bulunamadı: {fileName}";
            return;
        }

        try
        {
            // Teknik tablolar için sabit yüksek kalite (küçük yazılar var)
            byte[] rawBytes;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                rawBytes = ms.ToArray();
            }
            var optimized = ImageOptimizer.Optimize(rawBytes, ImageOptimizer.Preset.TechnicalTable);
            using var optimizedStream = new MemoryStream(optimized);

            var picture = ws.AddPicture(optimizedStream, Path.ChangeExtension(fileName, ".jpg"));
            picture.MoveTo(ws.Cell(startRow + 1, startCol), 5, 5);

            if (fileName.Contains("flexiva", StringComparison.OrdinalIgnoreCase))
                picture.WithSize(780, 580);
            else
                picture.WithSize(760, 430);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gömülü görsel eklenemedi: {File}", fileName);
            ws.Cell(startRow + 1, startCol).Value = $"Görsel eklenemedi: {fileName}";
        }
    }

    /// <summary>
    /// Excel sütununun piksel genişliğini hesaplar.
    /// Formül: Truncate(((256*W + Truncate(128/MDW)) / 256) * MDW)
    /// MDW = Max Digit Width — Calibri 11pt, 96 DPI ≈ 7 px.
    /// </summary>
    private static int GetColumnPixelWidth(IXLColumn col)
    {
        const double mdw = 7.0;
        return (int)Math.Truncate(
            ((256.0 * col.Width + Math.Truncate(128.0 / mdw)) / 256.0) * mdw);
    }

    /// <summary>
    /// startCol – endCol arasındaki sütunların toplam piksel genişliği.
    /// </summary>
    private static int GetBlockPixelWidth(IXLWorksheet ws, int startCol, int endCol)
    {
        int total = 0;
        for (int c = startCol; c <= endCol; c++)
            total += GetColumnPixelWidth(ws.Column(c));
        return total;
    }

    /// <summary>Tek bir belge kartının bilgi satırını çizer (label + value).</summary>
    private static void AddInfoRowCard(
        IXLWorksheet ws, ref int row, int cardCol, int cardWidthCols, string label, string value)
    {
        ws.Cell(row, cardCol).Value = label;
        ws.Cell(row, cardCol).Style.Font.Bold = true;
        ws.Cell(row, cardCol).Style.Fill.BackgroundColor = XLColor.FromArgb(245, 245, 245);
        ws.Range(row, cardCol + 1, row, cardCol + cardWidthCols - 1).Merge().Value = value;
        ApplyThinBorder(ws.Range(row, cardCol, row, cardCol + cardWidthCols - 1));
        row++;
    }

    private static void BuildUnsortedSheet(IXLWorksheet ws, List<ServiceForm> forms)
    {
        ws.Cell(1, 1).Value = "MAĞAZA ATANMAMIŞ FORMLAR";
        ws.Cell(1, 1).Style.Font.Bold = true;
        int row = 3;
        foreach (var f in forms)
        {
            ws.Cell(row, 1).Value = f.OriginalFileName;
            ws.Cell(row, 2).Value = f.UploadedAt.ToString("dd.MM.yyyy");
            ws.Cell(row, 3).Value = f.Status.ToString();
            row++;
        }
    }

    private static void AddInfoRow(IXLWorksheet ws, ref int row, string label, string value)
    {
        ws.Cell(row, 1).Value = label;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(245, 245, 245);
        ws.Range(row, 2, row, 3).Merge().Value = value;
        ApplyThinBorder(ws.Range(row, 1, row, 3));
        row++;
    }

    private static void ApplyThinBorder(IXLRange range)
    {
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromArgb(189, 195, 199);
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorderColor = XLColor.FromArgb(189, 195, 199);
    }

    /// <summary>
    /// Mağaza adından geçerli, benzersiz bir Excel sayfa adı üretir ve usedNames'e ekler.
    /// Geçersiz karakterler temizlenir, 31 karakter sınırına uyulur,
    /// çakışma varsa _1, _2 ... soneki eklenir.
    /// </summary>
    private static string CreateSafeWorksheetName(string storeName, HashSet<string> usedNames)
    {
        var baseName = SanitizeSheetName(storeName);
        if (!usedNames.Contains(baseName))
        {
            usedNames.Add(baseName);
            return baseName;
        }
        int suffix = 1;
        while (true)
        {
            var suffixStr = $"_{suffix}";
            var candidate = baseName.Length + suffixStr.Length <= 31
                ? baseName + suffixStr
                : baseName[..(31 - suffixStr.Length)] + suffixStr;
            if (!usedNames.Contains(candidate))
            {
                usedNames.Add(candidate);
                return candidate;
            }
            suffix++;
        }
    }

    private static string SanitizeSheetName(string name)
    {
        var invalid = new[] { '/', '\\', '?', '*', '[', ']', ':' };
        var sanitized = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return sanitized.Length > 31 ? sanitized[..31] : sanitized;
    }

    private void BuildSummarySheet(
        IXLWorksheet ws,
        ProgressClaim claim,
        string companyName,
        List<(string SheetName, string StoreName, string StoreCode, decimal Total, List<int> KdvTotalRows, int StoreTotalRow)> storeData)
    {
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.Column(1).Width = 8;   // No
        ws.Column(2).Width = 50;  // Mağaza Adı
        ws.Column(3).Width = 24;  // Düzenlenen Hakediş

        int row = 1;

        // ─── Üst başlık ────────────────────────────────────────────────────
        var monthName = claim.Month >= 1 && claim.Month <= 12
            ? TurkishMonths[claim.Month] : claim.Month.ToString();
        var headerText = $"{claim.Year} {monthName} AYI {companyName.ToUpper()} SERVİS HAKEDİŞİ";

        var titleRange = ws.Range(row, 1, row, 3);
        titleRange.Merge();
        titleRange.Value = headerText;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 13;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        titleRange.Style.Fill.BackgroundColor = XLColor.FromArgb(21, 96, 189);
        titleRange.Style.Font.FontColor = XLColor.White;
        ws.Row(row).Height = 30;
        ApplyThinBorder(titleRange);
        row++;

        row++; // boşluk

        // ─── Tablo başlıkları ───────────────────────────────────────────────
        ws.Cell(row, 1).Value = "No";
        ws.Cell(row, 2).Value = "Mağaza Adı";
        ws.Cell(row, 3).Value = "Düzenlenen Hakediş";
        for (int c = 1; c <= 3; c++)
        {
            ws.Cell(row, c).Style.Font.Bold = true;
            ws.Cell(row, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromArgb(52, 73, 94);
            ws.Cell(row, c).Style.Font.FontColor = XLColor.White;
        }
        ApplyThinBorder(ws.Range(row, 1, row, 3));
        row++;

        // ─── Mağaza satırları ───────────────────────────────────────────────
        if (storeData.Count == 0)
        {
            ws.Range(row, 1, row, 3).Merge();
            var emptyCell = ws.Cell(row, 1);
            emptyCell.Value = "Bu hakedişte kayıtlı mağaza bulunamadı.";
            emptyCell.Style.Font.Italic = true;
            emptyCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            return;
        }

        decimal grandTotal = 0;
        int no = 1;
        int firstStoreRow = row;

        foreach (var (sheetName, storeName, storeCode, total, kdvTotalRows, storeTotalRow) in storeData)
        {
            bool isEven = (no % 2 == 0);
            var rowColor = isEven ? XLColor.FromArgb(235, 241, 248) : XLColor.White;

            ws.Cell(row, 1).Value = no;
            ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            var displayName = string.IsNullOrWhiteSpace(storeCode)
                ? storeName
                : $"{storeName} {storeCode}";
            var nameCell = ws.Cell(row, 2);
            var safeDisplay = displayName.Replace("\"", "\"\"");
            var safeSheet = sheetName.Replace("'", "''");
            nameCell.FormulaA1 = $"=HYPERLINK(\"#'{safeSheet}'!A1\",\"{safeDisplay}\")";
            nameCell.Style.Font.FontColor = XLColor.FromArgb(0, 70, 173);
            nameCell.Style.Font.Underline = XLFontUnderlineValues.Single;

            // Çoklu form varsa mağaza toplam satırına bağlan, tek formsa kdvTotal'a
            var storeFormula = BuildStoreTotalFormulaByRow(sheetName, storeTotalRow, kdvTotalRows);
            if (storeFormula != null)
                ws.Cell(row, 3).FormulaA1 = storeFormula;
            else
                ws.Cell(row, 3).Value = total;
            ws.Cell(row, 3).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            ws.Range(row, 1, row, 3).Style.Fill.BackgroundColor = rowColor;
            ApplyThinBorder(ws.Range(row, 1, row, 3));

            grandTotal += total;
            no++;
            row++;
        }

        // ─── Genel Toplam ───────────────────────────────────────────────────
        ws.Range(row, 1, row, 2).Merge().Value = "GENEL TOPLAM (KDV HARİÇ)";
        ws.Range(row, 1, row, 2).Style.Font.Bold = true;
        ws.Range(row, 1, row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.FromArgb(44, 62, 80);
        ws.Range(row, 1, row, 2).Style.Font.FontColor = XLColor.White;

        int lastStoreRow = row - 1;
        if (lastStoreRow >= firstStoreRow)
            ws.Cell(row, 3).FormulaA1 = $"=SUM(C{firstStoreRow}:C{lastStoreRow})";
        else
            ws.Cell(row, 3).Value = grandTotal;
        ws.Cell(row, 3).Style.NumberFormat.Format = MoneyFormat;
        ws.Cell(row, 3).Style.Font.Bold = true;
        ws.Cell(row, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(row, 3).Style.Fill.BackgroundColor = XLColor.FromArgb(52, 152, 219);
        ws.Cell(row, 3).Style.Font.FontColor = XLColor.White;
        ApplyThinBorder(ws.Range(row, 1, row, 3));
    }

    // ─── Excel formül yardımcıları ──────────────────────────────────────────

    private static string? BuildAutoFittingFormula(
        ServiceItem item,
        Dictionary<string, int> parentKeyToExcelRow,
        List<int> bp30Rows, List<int> bp80Rows, List<int> galvSteelRows,
        List<int> galvDuctRows)
    {
        static string SumRef(List<int> rows) =>
            rows.Count == 1
                ? $"H{rows[0]}"
                : $"SUM({string.Join(",", rows.Select(r => $"H{r}"))})";

        switch (item.AutoGeneratedType)
        {
            case "BlackPipeFitting30":
            case "SiyahBoruFittings30":
                return bp30Rows.Count > 0 ? $"={SumRef(bp30Rows)}*0.3" : null;

            case "BlackPipeFitting80":
            case "SiyahBoruFittings80":
                return bp80Rows.Count > 0 ? $"={SumRef(bp80Rows)}*0.8" : null;

            case "GalvanizedSteelPipeFitting40":
                return galvSteelRows.Count > 0 ? $"={SumRef(galvSteelRows)}*0.4" : null;

            case "GalvanizedDuctFitting30":
                // Grup formülü: tüm galvaniz hava kanalı kalemlerinin MALZEME TUTARI (C×E) toplamı × 0.30
                if (galvDuctRows.Count > 0)
                {
                    var parts = galvDuctRows.Select(r => $"C{r}*E{r}");
                    return galvDuctRows.Count == 1
                        ? $"={parts.First()}*0.3"
                        : $"=({string.Join("+", parts)})*0.3";
                }
                // Eski bireysel satır fallback (ParentItemKey varsa)
                if (!string.IsNullOrEmpty(item.ParentItemKey) &&
                    parentKeyToExcelRow.TryGetValue(item.ParentItemKey, out int prDuct))
                    return $"=C{prDuct}*E{prDuct}*0.3";
                return null;

            case "InsulationMounting":
                if (!string.IsNullOrEmpty(item.ParentItemKey) &&
                    parentKeyToExcelRow.TryGetValue(item.ParentItemKey, out int prIns))
                    return $"=C{prIns}*E{prIns}*0.15";
                return null;

            default:
                return null;
        }
    }

    private static string? BuildStoreTotalFormula(string sheetName, List<int> kdvTotalRows)
    {
        if (kdvTotalRows.Count == 0) return null;
        var safeSheet = sheetName.Replace("'", "''");
        if (kdvTotalRows.Count == 1)
            return $"='{safeSheet}'!H{kdvTotalRows[0]}";
        return $"=SUM({string.Join(",", kdvTotalRows.Select(r => $"'{safeSheet}'!H{r}"))})";
    }

    /// <summary>
    /// İcmal sayfası için mağaza toplamını referans alan formülü üretir.
    /// Çoklu form: StoreTotalRow hücresine bağlan.
    /// Tek form veya storeTotalRow == -1: ilk kdvTotalRow'a bağlan.
    /// </summary>
    private static string? BuildStoreTotalFormulaByRow(string sheetName, int storeTotalRow, List<int> kdvTotalRows)
    {
        if (storeTotalRow > 0)
        {
            var safeSheet = sheetName.Replace("'", "''");
            return $"='{safeSheet}'!H{storeTotalRow}";
        }
        return BuildStoreTotalFormula(sheetName, kdvTotalRows);
    }

    private static string ExportNrm(string s) =>
        s.Replace('ı', 'I').Replace('İ', 'I')
         .Replace('ğ', 'G').Replace('Ğ', 'G')
         .Replace('ü', 'U').Replace('Ü', 'U')
         .Replace('ş', 'S').Replace('Ş', 'S')
         .Replace('ö', 'O').Replace('Ö', 'O')
         .Replace('ç', 'C').Replace('Ç', 'C')
         .ToUpperInvariant().Trim();

    private static string ExportNrmPipe(string s)
        => ExportNrm(s)
            .Replace("\u00bd", " 1/2")   // ½ → 1/2
            .Replace("\u00bc", " 1/4");  // ¼ → 1/4

    private static bool IsExportBlackSteelPipe(string desc)
    {
        var s = ExportNrm(desc);
        return s.Contains("DIKISLI") && s.Contains("SIYAH") && s.Contains("BORU");
    }

    private static bool ExportPipeSizeMatch(string s, string digit)
    {
        if (s.Contains(digit + "\"")) return true;
        if (s.Contains(digit + " \"")) return true;
        if (s.Contains(digit + " INC")) return true;
        int idx = -1;
        while ((idx = s.IndexOf(digit, idx + 1)) >= 0)
        {
            bool prevOk = idx == 0 || (!char.IsDigit(s[idx - 1]) && s[idx - 1] != '/');
            if (!prevOk) continue;
            int after = idx + digit.Length;
            bool nextOk = after >= s.Length || (!char.IsDigit(s[after]) && s[after] != '/');
            if (nextOk) return true;
        }
        return false;
    }

    private static decimal? ExportExtractPipeDiameter(string nrmPipeStr)
    {
        var s = nrmPipeStr;
        if (s.Contains("2 1/2")) return 2.5m;
        if (s.Contains("1 1/4")) return 1.25m;
        if (s.Contains("1 1/2")) return 1.5m;
        if (s.Contains("3/4"))   return 0.75m;
        if (s.Contains("1/2"))   return 0.5m;
        if (ExportPipeSizeMatch(s, "10")) return 10m;
        if (ExportPipeSizeMatch(s, "8"))  return 8m;
        if (ExportPipeSizeMatch(s, "6"))  return 6m;
        if (ExportPipeSizeMatch(s, "5"))  return 5m;
        if (ExportPipeSizeMatch(s, "4"))  return 4m;
        if (ExportPipeSizeMatch(s, "3"))  return 3m;
        if (ExportPipeSizeMatch(s, "2"))  return 2m;
        if (ExportPipeSizeMatch(s, "1"))  return 1m;
        return null;
    }

    private static bool IsExportGalvSteelPipe(string desc)
    {
        var s = ExportNrm(desc);
        return s.Contains("GALVANIZLI") && s.Contains("CELIK") && s.Contains("BORU");
    }

    // ── Excel bölüm sınıflandırma yardımcıları ─────────────────────────────────

    /// <summary>
    /// Açıklama metninin başındaki ürün kodunu (BDTX 125, BDKF 60-30 vb.)
    /// USD hesaplama kısmından ayırarak döndürür.
    /// Örnek: "BDTX 125 77 USD x %20 iskonto = ..." → "BDTX 125"
    /// </summary>
    private static string ExtractShortProductCode(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return description ?? string.Empty;
        // İlk "N USD" kalıbını bul — hesaplama buradan başlar (ör. "77 USD", "259,20 USD")
        var match = Regex.Match(description, @"\s+\d[\d,\.]*\s+USD", RegexOptions.IgnoreCase);
        if (match.Success)
            return description[..match.Index].Trim();
        return description.Trim();
    }

    /// <summary>
    /// Döviz açıklama kuyruğundaki tekrarlı hesaplama metnini temizler.
    /// "61,60 USD x seçilen USD kuru 77,00 USD x %20 iskonto = 61,60 USD x 44,75 TL = 2.756,51 TL"
    /// → "61,60 USD x seçilen USD kuru 44,75 TL = 2.756,51 TL"
    /// </summary>
    private static string CleanCurrencyDescTail(string tail)
    {
        const string kurKey = "USD x seçilen USD kuru";
        int kurIdx = tail.IndexOf(kurKey, StringComparison.OrdinalIgnoreCase);
        if (kurIdx < 0) return tail;

        int afterKur = kurIdx + kurKey.Length;
        while (afterKur < tail.Length && tail[afterKur] == ' ') afterKur++;

        var prefix = tail[..afterKur]; // "61,60 USD x seçilen USD kuru "
        var suffix = tail[afterKur..]; // kur oranı veya tekrarlı hesaplama

        // suffix içindeki ilk "N,NN TL =" kalıbını bul → kur oranı buradan başlar
        var rateMatch = Regex.Match(suffix, @"\d[\d,\.]+\s+TL\s+=");
        if (rateMatch.Success)
        {
            int numStart = rateMatch.Index;
            // Sayı başlangıcına git
            while (numStart > 0 && (char.IsDigit(suffix[numStart - 1]) ||
                   suffix[numStart - 1] == ',' || suffix[numStart - 1] == '.'))
                numStart--;
            return prefix + suffix[numStart..].TrimStart();
        }

        return tail;
    }

    /// <summary>
    /// B kolonundaki açıklama metnini belirtilen karakter genişliğinde
    /// kelime sınırlarında \n ile sarar.
    /// </summary>
    private static string WrapDescriptionText(string text, int maxLineLength = 75)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var currentLine = "";

        foreach (var word in words)
        {
            var testLine = string.IsNullOrWhiteSpace(currentLine)
                ? word
                : currentLine + " " + word;

            if (testLine.Length > maxLineLength)
            {
                if (!string.IsNullOrWhiteSpace(currentLine))
                    lines.Add(currentLine.Trim());

                currentLine = word;
            }
            else
            {
                currentLine = testLine;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentLine))
            lines.Add(currentLine.Trim());

        return string.Join("\n", lines);
    }

    /// <summary>
    /// B kolonunda wrap text kapalı; satır yüksekliği Calibri 11pt için standart değere sabitlenir.
    /// </summary>
    private static void ApplyDescriptionWrapAndHeight(IXLWorksheet ws, int row)
    {
        ws.Cell(row, 2).Style.Alignment.WrapText = false;
        ws.Cell(row, 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(row).Height = 15.75;
    }

    /// <summary>
    /// Döviz bazlı kalem açıklamasını Excel için oluşturur.
    /// </summary>
    private static string BuildItemExcelDescription(ServiceItem item)
    {
        if (item.IsInvoiceBased)
            return item.Description + " (Fatura +%10)";

        if (item.IsCurrencyBased
            && item.DiscountedUsdPrice.HasValue
            && item.ExchangeRate.HasValue)
        {
            var productCode   = ExtractShortProductCode(item.Description);
            var discountedUsd = item.DiscountedUsdPrice.Value;
            var exchangeRate  = item.ExchangeRate.Value;
            var materialTl    = item.MaterialPrice;
            var dateToShow    = item.ExchangeRateActualDate ?? item.ExchangeRateDate;

            var datePart = dateToShow.HasValue
                ? $" (Kur Tarihi: {dateToShow.Value:dd.MM.yyyy})"
                : string.Empty;

            return $"{productCode} = {discountedUsd.ToString("N2", TrCulture)} USD x seçilen USD kuru " +
                   $"{exchangeRate.ToString("N2", TrCulture)} TL = " +
                   $"{materialTl.ToString("N2", TrCulture)} TL{datePart}";
        }

        if (item.IsCurrencyBased)
        {
            // Sayısal alanlar yoksa ExportDescription'ı parse ederek kısa formata dönüştür
            var baseDesc = !string.IsNullOrWhiteSpace(item.ExportDescription)
                ? item.ExportDescription
                : item.Description;

            // "259,20 USD x seçilen USD kuru 44,76 TL = 11.601,61 TL (Kur Tarihi: ...)" kısmını ayıkla
            const string secilenKey = "USD x seçilen USD kuru";
            var lastIdx = baseDesc.LastIndexOf(secilenKey, StringComparison.OrdinalIgnoreCase);
            if (lastIdx >= 0)
            {
                // secilenKey'den geriye sayı başlangıcına git
                var pos = lastIdx - 1;
                while (pos > 0 && baseDesc[pos] == ' ') pos--;
                while (pos > 0 && (char.IsDigit(baseDesc[pos - 1]) || baseDesc[pos - 1] == ',' || baseDesc[pos - 1] == '.'))
                    pos--;
                var tail = baseDesc.Substring(pos).TrimStart();
                var shortDesc = $"{ExtractShortProductCode(item.Description)} = {CleanCurrencyDescTail(tail)}";
                var dateToShow = item.ExchangeRateActualDate ?? item.ExchangeRateDate;
                if (dateToShow.HasValue && !shortDesc.Contains("Kur Tarihi", StringComparison.OrdinalIgnoreCase))
                    shortDesc += $" (Kur Tarihi: {dateToShow.Value:dd.MM.yyyy})";
                return shortDesc;
            }

            var dateToShowFallback = item.ExchangeRateActualDate ?? item.ExchangeRateDate;
            if (dateToShowFallback.HasValue && !baseDesc.Contains("Kur Tarihi", StringComparison.OrdinalIgnoreCase))
                return baseDesc + $" (Kur Tarihi: {dateToShowFallback.Value:dd.MM.yyyy})";
            return baseDesc;
        }

        return item.Description;
    }

    private static bool ExportIsServiceFee(ServiceItem item)
        => ExportNrm(item.Description).Contains("SERVIS UCRETI") && !item.IsInvoiceBased;

    private static bool ExportIsInvoiceBased(ServiceItem item)
        => item.IsInvoiceBased
        || item.AutoGeneratedType == "InvoicePlus10"
        || ExportNrm(item.Description).Contains("FATURA +%10");

    /// <summary>
    /// Galvaniz Sac Dikdörtgen Hava Kanalı tespiti — AutoRuleTrigger kaydedilmemiş (null)
    /// eski kalemler için açıklama bazlı fallback.
    /// </summary>
    private static bool IsExportGalvanizedDuct(string desc)
    {
        var s = ExportNrm(desc);
        if (!(s.Contains("GALVANIZ") && s.Contains("KANAL"))) return false;
        return s.Contains("0.5 MM") || s.Contains("0.5MM") ||
               s.Contains("0,5 MM") || s.Contains("0,5MM") ||
               s.Contains("0.6 MM") || s.Contains("0.6MM") ||
               s.Contains("0,6 MM") || s.Contains("0,6MM") ||
               s.Contains("0.8 MM") || s.Contains("0.8MM") ||
               s.Contains("0,8 MM") || s.Contains("0,8MM") ||
               s.Contains("1.0 MM") || s.Contains("1.0MM") ||
               s.Contains("1,0 MM") || s.Contains("1,0MM") ||
               s.Contains("<= 250")  || s.Contains("<=250")  ||
               s.Contains("<= 499")  || s.Contains("<=499")  ||
               s.Contains("<= 990")  || s.Contains("<=990")  ||
               s.Contains("<= 1490") || s.Contains("<=1490");
    }

}
