using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Firmanın gönderdiği aylık soğutma bakım hakediş Excel'ini okur. Gerçek dosya çok sayfalıdır
/// (GENEL ICMAL / MALZ HAKEDIS / ÇALIŞMA / Mağazalar); asıl satır verisi "MALZEME ADI" + "MİKTARI"
/// başlıklarını içeren sayfada bulunur — sayfa adı yıldan yıla değişebileceği için başlık taranarak
/// bulunur. "ÇALIŞMA" (ve benzeri firma iç çalışma) sayfaları ASLA fiyat kaynağı olarak kullanılmaz;
/// sadece varsa EUR/TL kuru için kullanıcıya bir ÖNERİ çıkarmak amacıyla taranır — kullanıcı onayı
/// olmadan hiçbir kur otomatik uygulanmaz.
/// </summary>
public static class ProgressPaymentExcelParser
{
    private static readonly string[] NeverSourceSheetHints = { "calisma", "magaza" };

    public static ProgressPaymentImportPreviewDto Parse(Stream stream, string fileName)
    {
        var preview = new ProgressPaymentImportPreviewDto();
        using var wb = new XLWorkbook(stream);

        // ── Başlık/dönem/firma bilgisi ve satır sayfası tespiti ──────────
        IXLWorksheet? dataSheet = null;
        Dictionary<string, int>? colMap = null;
        int headerRowNumber = 0;

        foreach (var ws in wb.Worksheets)
        {
            if (ws.IsEmpty()) continue;
            var normName = Normalize(ws.Name);
            if (NeverSourceSheetHints.Any(h => normName.Contains(h))) continue;

            var (found, cols, rowNo) = TryFindItemHeaderRow(ws);
            if (found)
            {
                dataSheet = ws;
                colMap = cols;
                headerRowNumber = rowNo;
                break;
            }
        }

        if (dataSheet is null || colMap is null)
        {
            preview.Errors.Add("Hakediş satırlarını içeren sayfa bulunamadı. 'MALZEME ADI' ve 'MİKTARI' başlıklarını içeren bir sayfa gereklidir.");
            return preview;
        }

        // Firma/dönem bilgisi — data sheet'in üst köşesinden (ilk 10 satır/10 sütun) çıkar
        (preview.DetectedCompanyName, preview.DetectedYear, preview.DetectedMonth, preview.DetectedPeriodLabel)
            = ExtractHeaderInfo(dataSheet);

        preview.DetectedClaimTypeName = GuessClaimType(fileName);

        // ── GENEL ICMAL benzeri özet sayfasından firma toplamını çek ─────
        var icmalSheet = wb.Worksheets.FirstOrDefault(s => Normalize(s.Name).Contains("icmal"));
        if (icmalSheet != null)
            preview.DetectedCompanyGrandTotal = SumIcmalTotal(icmalSheet);

        // ── EUR kuru önerisi (yalnızca öneri — kullanıcı onaylamadan kullanılmaz) ──
        var (rate, source) = FindSuggestedEurRate(wb);
        if (rate.HasValue)
        {
            preview.SuggestedEurRate = rate.Value.ToString("0.####", CultureInfo.InvariantCulture);
            preview.SuggestedEurRateSource = source;
        }

        // ── Satırları oku ─────────────────────────────────────────────
        colMap.TryGetValue("magazakodu", out int colMagazaKodu);
        colMap.TryGetValue("magazaadi", out int colMagazaAdi);
        colMap.TryGetValue("format", out int colFormat);
        colMap.TryGetValue("tarih", out int colTarih);
        colMap.TryGetValue("bakimformno", out int colFormNo);
        colMap.TryGetValue("malzemekodu", out int colMalzemeKodu);
        colMap.TryGetValue("malzemeadi", out int colMalzemeAdi);
        colMap.TryGetValue("malzemetipi", out int colMalzemeTipi);
        colMap.TryGetValue("miktari", out int colMiktar);
        colMap.TryGetValue("birimi", out int colBirim);
        colMap.TryGetValue("fiyat", out int colFiyat);
        colMap.TryGetValue("toplam", out int colToplam);

        var items = new List<ProgressPaymentCheckItemDto>();
        var storeCodes = new HashSet<string>();
        int startRow = headerRowNumber + 1;

        foreach (var row in dataSheet.RowsUsed().Where(r => r.RowNumber() >= startRow))
        {
            preview.TotalRowsRead++;

            var malzemeAdi = CellText(row, colMalzemeAdi);
            var malzemeKodu = CellText(row, colMalzemeKodu);
            if (string.IsNullOrWhiteSpace(malzemeAdi) && string.IsNullOrWhiteSpace(malzemeKodu))
                continue; // boş / ayraç satırı

            var magazaKodu = CellText(row, colMagazaKodu);
            var magazaAdi = CellText(row, colMagazaAdi);
            if (!string.IsNullOrWhiteSpace(magazaKodu)) storeCodes.Add(magazaKodu);

            bool isService = !decimal.TryParse(malzemeKodu, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
                              && !string.IsNullOrWhiteSpace(malzemeKodu);

            bool hasQuantity = TryGetDecimal(row.Cell(Math.Max(colMiktar, 1)), out decimal miktar);
            if (colMiktar == 0 || !hasQuantity) preview.MissingQuantityCount++;

            TryGetDecimal(colFiyat > 0 ? row.Cell(colFiyat) : null, out decimal fiyat);
            TryGetDecimal(colToplam > 0 ? row.Cell(colToplam) : null, out decimal toplam);
            if (toplam == 0 && fiyat != 0 && miktar != 0) toplam = fiyat * miktar;

            DateTime? tarih = null;
            if (colTarih > 0 && row.Cell(colTarih).TryGetValue<DateTime>(out var dt)) tarih = dt;

            var malzemeTipiTxt = CellText(row, colMalzemeTipi);
            if (malzemeTipiTxt == "0") malzemeTipiTxt = string.Empty; // servis satırlarında anlamsız 0 dolduruluyor

            items.Add(new ProgressPaymentCheckItemDto
            {
                SheetName = dataSheet.Name,
                SourceRowNumber = row.RowNumber(),
                StoreCode = string.IsNullOrWhiteSpace(magazaKodu) ? null : magazaKodu,
                StoreName = string.IsNullOrWhiteSpace(magazaAdi) ? null : magazaAdi,
                StoreFormat = CellText(row, colFormat) is { Length: > 0 } fmt ? fmt : null,
                VisitDate = tarih,
                MaintenanceFormNo = CellText(row, colFormNo) is { Length: > 0 } fn ? fn : null,
                OriginalItemCode = string.IsNullOrWhiteSpace(malzemeKodu) ? null : malzemeKodu,
                OriginalMaterialName = malzemeAdi.Trim(),
                OriginalMaterialSpec = string.IsNullOrWhiteSpace(malzemeTipiTxt) ? null : malzemeTipiTxt.Trim(),
                IsServiceItem = isService,
                Quantity = miktar,
                Unit = CellText(row, colBirim) is { Length: > 0 } u ? u : null,
                CompanyUnitPrice = fiyat,
                CompanyLineTotal = toplam,
                MatchStatus = MaterialMatchStatus.Unmatched,
                ControlStatus = CheckItemControlStatus.KontrolGerekli,
            });

            if (isService) preview.ServiceLineCount++; else preview.MaterialLineCount++;
        }

        preview.StoreCount = storeCodes.Count;
        preview.Items = items;
        preview.DebugMessages.Add($"Veri sayfası: '{dataSheet.Name}', başlık satırı: {headerRowNumber}.");
        return preview;
    }

    // ------------------------------------------------------------------ //
    private static (bool found, Dictionary<string, int> cols, int rowNo) TryFindItemHeaderRow(IXLWorksheet ws)
    {
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int searchLimit = Math.Min(20, lastRow);

        for (int r = 1; r <= searchLimit; r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;

            var cols = new Dictionary<string, int>();
            foreach (var cell in row.CellsUsed())
            {
                var norm = Normalize(cell.GetString());
                var col = cell.Address.ColumnNumber;
                if (norm.Contains("magazakodu")) cols["magazakodu"] = col;
                else if (norm.Contains("magazaadi")) cols["magazaadi"] = col;
                else if (norm == "format") cols["format"] = col;
                else if (norm == "tarih") cols["tarih"] = col;
                else if (norm.Contains("bakimformno") || norm.Contains("servisformno")) cols["bakimformno"] = col;
                else if (norm.Contains("malzemekodu")) cols["malzemekodu"] = col;
                else if (norm.Contains("malzemeadi")) cols["malzemeadi"] = col;
                else if (norm.Contains("malzemetipi")) cols["malzemetipi"] = col;
                else if (norm.Contains("miktari") || norm == "miktar") cols["miktari"] = col;
                else if (norm.Contains("birimi") || norm == "birim") cols["birimi"] = col;
                else if (norm == "fiyat" || norm.Contains("birimfiyat")) cols["fiyat"] = col;
                else if (norm == "toplam") cols["toplam"] = col;
            }

            if (cols.ContainsKey("malzemeadi") && cols.ContainsKey("miktari"))
            {
                // Mağaza kodu genelde "MAĞAZA ADI" kolonunun hemen solunda, başlıksız durur.
                if (!cols.ContainsKey("magazakodu") && cols.TryGetValue("magazaadi", out int adiCol) && adiCol > 1)
                {
                    var leftHeader = Normalize(row.Cell(adiCol - 1).GetString());
                    if (string.IsNullOrEmpty(leftHeader))
                    {
                        // solundaki veri satırında sayısal değer var mı kontrol et (mağaza kodu tahmini)
                        var checkRow = ws.Row(r + 2);
                        if (!checkRow.IsEmpty() && checkRow.Cell(adiCol - 1).TryGetValue<double>(out _))
                            cols["magazakodu"] = adiCol - 1;
                    }
                }
                return (true, cols, r);
            }
        }
        return (false, new Dictionary<string, int>(), 0);
    }

    private static (string? company, int? year, int? month, string? periodLabel) ExtractHeaderInfo(IXLWorksheet ws)
    {
        string? company = null;
        int? year = null, month = null;
        int scanRows = Math.Min(10, ws.LastRowUsed()?.RowNumber() ?? 10);
        int scanCols = Math.Min(10, ws.LastColumnUsed()?.ColumnNumber() ?? 10);

        for (int r = 1; r <= scanRows; r++)
        {
            for (int c = 1; c <= scanCols; c++)
            {
                var cell = ws.Cell(r, c);
                var norm = Normalize(cell.GetString());
                if (norm.Contains("firmaunvani"))
                {
                    for (int cc = c + 1; cc <= scanCols + 4; cc++)
                    {
                        var txt = ws.Cell(r, cc).GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(txt)) { company = txt; break; }
                    }
                }
                else if (norm == "donem")
                {
                    for (int cc = c + 1; cc <= scanCols + 4; cc++)
                    {
                        if (ws.Cell(r, cc).TryGetValue<DateTime>(out var dt))
                        {
                            year = dt.Year; month = dt.Month; break;
                        }
                    }
                }
            }
        }

        string? periodLabel = year.HasValue && month.HasValue
            ? $"{TurkishMonthName(month.Value)} {year.Value}"
            : null;
        return (company, year, month, periodLabel);
    }

    private static decimal SumIcmalTotal(IXLWorksheet ws)
    {
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int searchLimit = Math.Min(15, lastRow);
        int totalCol = 0;

        for (int r = 1; r <= searchLimit; r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;
            foreach (var cell in row.CellsUsed())
            {
                if (Normalize(cell.GetString()).Contains("toplamfiyat"))
                {
                    totalCol = cell.Address.ColumnNumber;
                    break;
                }
            }
            if (totalCol > 0)
            {
                decimal sum = 0;
                foreach (var dataRow in ws.RowsUsed().Where(x => x.RowNumber() > r))
                {
                    if (TryGetDecimal(dataRow.Cell(totalCol), out var v)) sum += v;
                }
                return sum;
            }
        }
        return 0;
    }

    /// <summary>Firmanın kendi çalışma sayfasında (varsa) "EURO (... ort.)" gibi bir etiketin
    /// yanındaki sayıyı sadece ÖNERİ olarak okur. Fiyat/hesap için asla otomatik kullanılmaz.</summary>
    private static (decimal? rate, string? source) FindSuggestedEurRate(XLWorkbook wb)
    {
        foreach (var ws in wb.Worksheets)
        {
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow == 0) continue;

            int fromRow = Math.Max(1, lastRow - 15);
            for (int r = fromRow; r <= lastRow; r++)
            {
                for (int c = 1; c <= lastCol; c++)
                {
                    var cell = ws.Cell(r, c);
                    var txt = cell.GetString();
                    if (Regex.IsMatch(txt, "euro", RegexOptions.IgnoreCase))
                    {
                        for (int cc = c + 1; cc <= lastCol; cc++)
                        {
                            if (TryGetDecimal(ws.Cell(r, cc), out var v) && v > 0)
                                return (v, $"'{ws.Name}' sayfası, {txt.Trim()}");
                        }
                    }
                }
            }
        }
        return (null, null);
    }

    private static string GuessClaimType(string fileName)
    {
        var n = Normalize(fileName);
        if (n.Contains("sabitfiyat")) return "SABİT FİYAT";
        if (n.Contains("perybakim") || n.Contains("periyodikbakim")) return "PERİYODİK BAKIM";
        if (n.Contains("gaz")) return "GAZ KULLANIM";
        if (n.Contains("kismitadilat")) return "KISMİ TADİLAT";
        if (n.Contains("ilaveisler")) return "İLAVE İŞLER";
        if (n.Contains("evap")) return "EVAP TEMİN VE DEĞİŞİM";
        if (n.Contains("glikol")) return "GLİKOL KULLANIM";
        if (n.Contains("kompresor")) return "KOMPRESÖR TEMİN VE DEĞİŞİM";
        if (n.Contains("izleme")) return "İZLEME BEDELLERİ";
        return "Diğer";
    }

    private static string TurkishMonthName(int month) => month switch
    {
        1 => "Ocak", 2 => "Şubat", 3 => "Mart", 4 => "Nisan", 5 => "Mayıs", 6 => "Haziran",
        7 => "Temmuz", 8 => "Ağustos", 9 => "Eylül", 10 => "Ekim", 11 => "Kasım", 12 => "Aralık",
        _ => month.ToString()
    };

    private static string CellText(IXLRow row, int col)
        => col > 0 ? (row.Cell(col).GetString()?.Trim() ?? string.Empty) : string.Empty;

    private static bool TryGetDecimal(IXLCell? cell, out decimal value)
    {
        value = 0;
        if (cell is null || cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number)
        {
            value = (decimal)cell.GetDouble();
            return true;
        }
        var text = (cell.GetString() ?? string.Empty).Trim()
            .Replace("TL", "", StringComparison.OrdinalIgnoreCase)
            .Replace("€", "").Replace("₺", "");
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains(','))
        {
            var norm = text.Replace(".", "").Replace(",", ".");
            if (decimal.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;
        }
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string Normalize(string? input)
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
            if (mapped is ' ' or '.' or '(' or ')' or '-' or '_' or '/' or '\'' or '"')
                continue;
            sb.Append(mapped);
        }
        return sb.ToString();
    }
}
