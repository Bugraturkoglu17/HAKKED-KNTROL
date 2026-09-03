using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        using var wb = OpenWorkbook(ms.ToArray());

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

        // ── "Mağazalar" master sayfası: mağaza kodu → il eşlemesi (İLAVE İŞLER'de şehir içi/şehir dışı
        // servis bedeli TÜRÜNÜN doğru talep edilip edilmediğini Excel'den doğrulamak için gerekli — bkz.
        // AdditionalWorkComparisonStrategy. Bu sayfa ana veri sayfasında (MALZ HAKEDİŞ) yoktur, yalnızca
        // burada "İşyeri No"/"IlAdi" kolonlarıyla bulunur.) ──
        var storeCityByCode = BuildStoreCityMap(wb);

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
            storeCityByCode.TryGetValue(TextNormalizationHelper.NormalizeCode(magazaKodu), out var magazaIli);

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
                MaterialCellRef = colMalzemeAdi > 0 ? row.Cell(colMalzemeAdi).Address.ToString() : null,
                QuantityCellRef = colMiktar > 0 ? row.Cell(colMiktar).Address.ToString() : null,
                UnitPriceCellRef = colFiyat > 0 ? row.Cell(colFiyat).Address.ToString() : null,
                LineTotalCellRef = colToplam > 0 ? row.Cell(colToplam).Address.ToString() : null,
                StoreCode = string.IsNullOrWhiteSpace(magazaKodu) ? null : magazaKodu,
                StoreName = string.IsNullOrWhiteSpace(magazaAdi) ? null : magazaAdi,
                StoreFormat = CellText(row, colFormat) is { Length: > 0 } fmt ? fmt : null,
                StoreCity = string.IsNullOrWhiteSpace(magazaIli) ? null : magazaIli,
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
    //  HARİCİ BAĞLANTI (EXTERNAL LINK) ONARIMI
    // ------------------------------------------------------------------ //

    /// <summary>Bazı Excel dosyaları (genelde başka bir çalışma kitabından kopyala-yapıştırla oluşturulmuş
    /// hücreler nedeniyle) "harici bağlantı" (external link) meta verisi içerir — hücrelerin GÖRÜNEN
    /// değerleri statiktir, ama dosyanın içinde hâlâ "başka bir dosyaya referans" kaydı kalmıştır.
    /// ClosedXML bu meta veriyi ayrıştıramaz ve NotImplementedException fırlatır ("References from other
    /// files are not yet implemented") — dosyanın gerçek verisiyle hiçbir ilgisi olmayan bir hatadır.
    /// ÖNEMLİ (gerçek olayda tespit edildi): bu hata çoğunlukla <see cref="XLWorkbook"/> CONSTRUCTOR'INDA
    /// değil, İLK formül hücresi bir yerlerde ".GetString()"/".GetDouble()" ile okunduğunda (ClosedXML'in
    /// CalcEngine'i o hücreyi hesaplamaya çalışırken) fırlatılıyor — yani "önce normal aç, hata alırsan
    /// onar" (reaktif) yaklaşımı ÇALIŞMAZ, çünkü açma işleminin kendisi başarılı görünüp asıl patlama
    /// çağrı zincirinin çok daha derininde (ör. FindSuggestedEurRate) gerçekleşir ve o noktada onarım artık
    /// devreye giremez. Bu yüzden PROAKTİF davranılır: dosya AÇILMADAN ÖNCE harici bağlantı içerip
    /// içermediği kontrol edilir, içeriyorsa onarım baştan (hiç normal açmayı denemeden) uygulanır — hücre
    /// DEĞERLERİ hiç etkilenmez, yalnızca artık kullanılmayan "başka dosyaya bağlantı"/formül kaydı
    /// silinir. Onarım başarısız olursa kullanıcıya elle nasıl düzelteceğini anlatan net bir Türkçe hata
    /// verir.</summary>
    private static XLWorkbook OpenWorkbook(byte[] bytes)
    {
        if (HasExternalLinks(bytes))
        {
            if (!TryStripExternalLinks(bytes, out var repaired))
            {
                throw new InvalidOperationException(
                    "Bu Excel dosyası başka bir çalışma kitabına (dosyaya) referans veren gizli bağlantılar " +
                    "içeriyor ve otomatik olarak temizlenemedi. Excel'de dosyayı açıp \"Veri\" sekmesinden " +
                    "\"Bağlantıları Düzenle\" → \"Bağlantıları Kaldır\" seçeneğini kullanıp tekrar kaydettikten " +
                    "sonra yeniden yükleyin.");
            }
            bytes = repaired;
        }

        try
        {
            return new XLWorkbook(new MemoryStream(bytes));
        }
        catch (NotImplementedException ex) when (ex.Message.Contains("References from other files", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Bu Excel dosyası başka bir çalışma kitabına (dosyaya) referans veren gizli bağlantılar " +
                "içeriyor ve otomatik olarak temizlenemedi. Excel'de dosyayı açıp \"Veri\" sekmesinden " +
                "\"Bağlantıları Düzenle\" → \"Bağlantıları Kaldır\" seçeneğini kullanıp tekrar kaydettikten " +
                "sonra yeniden yükleyin.", ex);
        }
    }

    /// <summary>Açmadan önce ucuz bir kontrol: .xlsx paketinde xl/externalLinks/* parçası var mı? Bu,
    /// TryStripExternalLinks'in de kullandığı aynı sinyaldir — burada AYRI tutulmasının sebebi, açma
    /// işleminden ÖNCE (bkz. OpenWorkbook'taki not) karar verebilmektir.</summary>
    private static bool HasExternalLinks(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            return zip.Entries.Any(e => e.FullName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false; // geçerli bir .xlsx/zip değilse burada karar vermeye çalışma, normal açma dener
        }
    }

    /// <summary>.xlsx bir ZIP paketidir — harici bağlantı kaydı DÖRT yerde durur: xl/externalLinks/* (asıl
    /// bağlantı parçaları), xl/workbook.xml içindeki &lt;externalReferences&gt; elemanı,
    /// xl/_rels/workbook.xml.rels + [Content_Types].xml içindeki ilgili kayıtlar — VE (gerçek olayda
    /// yakalanan, en kritik kısım) tek tek hücre FORMÜLLERİ. Yalnızca externalLinks parçalarını silmek
    /// YETERSİZDİR: "[1]SayfaAdı!..." biçiminde AÇIKÇA harici referans içeren formüller ayrı bir
    /// NotImplementedException kaynağıdır, AMA gerçek olayda ayrıca ClosedXML'in kendi hesaplama motoru
    /// (CalcEngine) sayfa-içi, TAMAMEN YEREL bir VLOOKUP formülünü ".GetString()" ile yeniden hesaplamaya
    /// çalışırken de (PrefixNode.GetWorksheet üzerinden) AYNI hatayı fırlattı — yani sorun yalnızca dış
    /// referanslarla sınırlı değil, ClosedXML'in formül hesaplama desteğinin genel bir sınırlaması.
    /// Uygulama hiçbir zaman canlı formül hesaplamıyor, yalnızca hücre DEĞERİNİ okuyor — bu yüzden onarım
    /// tetiklendiğinde TÜM &lt;f&gt; (formül) elemanları TAMAMEN silinir, hücrelerin önbelleklenmiş
    /// &lt;v&gt; DEĞERLERİ dokunulmadan kalır (tıpkı Excel'de "Değer Olarak Yapıştır" yapılmış gibi).</summary>
    private static bool TryStripExternalLinks(byte[] bytes, out byte[] repaired)
    {
        repaired = Array.Empty<byte>();
        try
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var srcMs = new MemoryStream(bytes))
            using (var srcZip = new ZipArchive(srcMs, ZipArchiveMode.Read))
            {
                foreach (var entry in srcZip.Entries)
                {
                    using var es = entry.Open();
                    using var buf = new MemoryStream();
                    es.CopyTo(buf);
                    entries[entry.FullName] = buf.ToArray();
                }
            }

            var removedRelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var externalLinkKeys = entries.Keys
                .Where(k => k.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (externalLinkKeys.Count == 0) return false; // temizlenecek bir şey yok, onarım gereksiz
            foreach (var key in externalLinkKeys) entries.Remove(key);

            // xl/calcChain.xml — hesaplama SIRASI önbelleğidir; hücrelerden <f> silinse bile bu dosya
            // "bu hücrede formül var, yeniden hesapla" kaydını taşımaya devam eder ve ClosedXML'in
            // CalcEngine'i (gerçek olayda AYNI NotImplementedException ile) yine tetiklenir. Formülü
            // olmayan bir çalışma kitabı için tamamen gereksiz/isteğe bağlı bir parçadır — güvenle silinir.
            entries.Remove("xl/calcChain.xml");

            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            // xl/workbook.xml — <externalReferences> elemanını ve içindeki r:id'leri topla, sonra sil.
            if (entries.TryGetValue("xl/workbook.xml", out var workbookXmlBytes))
            {
                var doc = XDocument.Load(new MemoryStream(workbookXmlBytes));
                var extRefsEl = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "externalReferences");
                if (extRefsEl != null)
                {
                    foreach (var er in extRefsEl.Elements())
                    {
                        var rid = er.Attribute(rNs + "id")?.Value;
                        if (!string.IsNullOrEmpty(rid)) removedRelIds.Add(rid);
                    }
                    extRefsEl.Remove();
                    using var outMs = new MemoryStream();
                    doc.Save(outMs);
                    entries["xl/workbook.xml"] = outMs.ToArray();
                }
            }

            // xl/_rels/workbook.xml.rels — externalLink tipindeki (veya yukarıda toplanan id'lere ait)
            // Relationship kayıtlarını sil.
            if (entries.TryGetValue("xl/_rels/workbook.xml.rels", out var relsBytes))
            {
                var doc = XDocument.Load(new MemoryStream(relsBytes));
                XNamespace pkgNs = "http://schemas.openxmlformats.org/package/2006/relationships";
                var toRemove = doc.Root?.Elements(pkgNs + "Relationship")
                    .Where(r => (r.Attribute("Type")?.Value.Contains("externalLink", StringComparison.OrdinalIgnoreCase) ?? false)
                                || (r.Attribute("Type")?.Value.Contains("calcChain", StringComparison.OrdinalIgnoreCase) ?? false)
                                || removedRelIds.Contains(r.Attribute("Id")?.Value ?? string.Empty))
                    .ToList();
                if (toRemove is { Count: > 0 })
                {
                    foreach (var r in toRemove) r.Remove();
                    using var outMs = new MemoryStream();
                    doc.Save(outMs);
                    entries["xl/_rels/workbook.xml.rels"] = outMs.ToArray();
                }
            }

            // [Content_Types].xml — externalLinks parçalarına ait Override kayıtlarını sil.
            if (entries.TryGetValue("[Content_Types].xml", out var ctBytes))
            {
                var doc = XDocument.Load(new MemoryStream(ctBytes));
                XNamespace ctNs = "http://schemas.openxmlformats.org/package/2006/content-types";
                var toRemove = doc.Root?.Elements(ctNs + "Override")
                    .Where(o => (o.Attribute("PartName")?.Value ?? string.Empty).StartsWith("/xl/externalLinks/", StringComparison.OrdinalIgnoreCase)
                                || (o.Attribute("PartName")?.Value ?? string.Empty) == "/xl/calcChain.xml")
                    .ToList();
                if (toRemove is { Count: > 0 })
                {
                    foreach (var o in toRemove) o.Remove();
                    using var outMs = new MemoryStream();
                    doc.Save(outMs);
                    entries["[Content_Types].xml"] = outMs.ToArray();
                }
            }

            // xl/worksheets/sheetN.xml — TÜM <f> (formül) elemanlarını sil; <v> (önbelleklenmiş değer)
            // elemanına dokunma. Yalnızca "[N]" harici referanslı formüllerle sınırlı tutulmadı (bkz.
            // yukarıdaki açıklama) — ClosedXML'in CalcEngine'i tamamen yerel formüllerde de aynı hatayı
            // fırlatabiliyor.
            var worksheetKeys = entries.Keys
                .Where(k => Regex.IsMatch(k, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
                .ToList();
            foreach (var wsKey in worksheetKeys)
            {
                var doc = XDocument.Load(new MemoryStream(entries[wsKey]));
                var formulaEls = doc.Descendants(mainNs + "f").ToList();
                if (formulaEls.Count == 0) continue;
                foreach (var f in formulaEls) f.Remove();
                using var outMs = new MemoryStream();
                doc.Save(outMs);
                entries[wsKey] = outMs.ToArray();
            }

            using var destMs = new MemoryStream();
            using (var destZip = new ZipArchive(destMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in entries)
                {
                    var entry = destZip.CreateEntry(name, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    entryStream.Write(content, 0, content.Length);
                }
            }
            repaired = destMs.ToArray();
            return true;
        }
        catch
        {
            return false; // onarım denemesi güvenli şekilde başarısız oldu — çağıran taraf orijinal hatayı fırlatır
        }
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

            // Not: TryAdd kullanılır — aynı anlama gelen birden fazla kolon varsa (ör. "ONAYA SUNULAN
            // MİKTAR" + "KESİLEN MİKTAR" + "ONAYLANAN MİKTAR" aynı satırda) SOLDAKİ/İLK kolon esas
            // alınır; sağdaki türetilmiş/ek kolonlar (kesinti, onay tutarı vb.) üzerine yazmaz.
            var cols = new Dictionary<string, int>();
            int sequenceNoCol = 0; // "SIRA NO" — asla gerçek bir form/belge numarası DEĞİLDİR (bkz. aşağı)
            foreach (var cell in row.CellsUsed())
            {
                var norm = Normalize(cell.GetString());
                var col = cell.Address.ColumnNumber;
                if (norm.Contains("magazakodu") || norm.Contains("magzkodu")) cols.TryAdd("magazakodu", col);
                else if (norm.Contains("magazaadi")) cols.TryAdd("magazaadi", col);
                else if (norm == "format") cols.TryAdd("format", col);
                else if (norm == "tarih") cols.TryAdd("tarih", col);
                else if (norm.Contains("bakimformno") || norm.Contains("servisformno") || norm.Contains("formno")
                         || norm.Contains("belgeno")) cols.TryAdd("bakimformno", col);
                // "SIRA NO" yalnızca satır sırası/indeksidir (1,2,3...), gerçek bir form/belge numarasıyla
                // HİÇ ilgisi yoktur — gerçek olayda "İlave İşler" dosyasında SIRA NO, BAKIM FORM NO'dan
                // önceki bir kolondaydı; eskiden ikisi aynı OR grubunda olduğu için TryAdd SIRA NO'yu
                // kilitleyip gerçek BAKIM FORM NO kolonunun hiç okunmamasına, dolayısıyla TÜM form
                // eşleştirmesinin (144 kalemin tamamı) çökmesine yol açıyordu. Artık yalnızca gerçek bir
                // form no kolonu HİÇ bulunamazsa en son çare olarak aşağıda kullanılır.
                else if (norm.Contains("sirano")) sequenceNoCol = col;
                else if (norm.Contains("malzemekodu")) cols.TryAdd("malzemekodu", col);
                else if (norm.Contains("malzemeadi")) cols.TryAdd("malzemeadi", col);
                else if (norm.Contains("malzemetipi")) cols.TryAdd("malzemetipi", col);
                else if (norm.Contains("miktar")) cols.TryAdd("miktari", col); // "MİKTARI", "MİKTAR", "ONAYA SUNULAN MİKTAR" vb. hepsi yakalanır
                else if (norm.Contains("birimi") || norm == "birim") cols.TryAdd("birimi", col);
                else if (norm == "fiyat" || norm.Contains("birimfiyat")) cols.TryAdd("fiyat", col);
                else if (norm == "toplam") cols.TryAdd("toplam", col);
            }

            // Gerçek bir form/belge no kolonu bu satırda hiç bulunamadıysa, SIRA NO'yu son çare olarak
            // kullan — bazı eski/basit hakediş dosyalarında form no ayrı bir kolon olarak hiç yoktur.
            if (!cols.ContainsKey("bakimformno") && sequenceNoCol > 0)
                cols["bakimformno"] = sequenceNoCol;

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

    /// <summary>"Mağazalar" master sayfasından (varsa) mağaza kodu → il eşlemesi çıkarır. Bu sayfa
    /// firmanın TÜM mağazalarını listeler (İşyeri No | Isyeri Adi | Bölge | Marka Adı | Format Adı |
    /// Adres | IlAdi | IlceAdi) — ana veri sayfasında (MALZ HAKEDIS) il bilgisi hiç yoktur, bu yüzden
    /// İLAVE İŞLER'in "şehir içi/şehir dışı" doğrulaması (Excel referanstır — bkz.
    /// AdditionalWorkComparisonStrategy) yalnızca bu sayfa üzerinden yapılabilir. Sayfa yoksa (bu
    /// kategoriye özel, diğer hakediş türlerinde gerekmez) boş sözlük döner — çağıran taraf
    /// TryGetValue ile güvenle kullanır.</summary>
    private static Dictionary<string, string> BuildStoreCityMap(XLWorkbook wb)
    {
        var map = new Dictionary<string, string>();
        var sheet = wb.Worksheets.FirstOrDefault(s => Normalize(s.Name).Contains("magazalar"));
        if (sheet is null || sheet.IsEmpty()) return map;

        int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        int searchLimit = Math.Min(10, lastRow);
        int colCode = 0, colCity = 0, headerRow = 0;

        for (int r = 1; r <= searchLimit; r++)
        {
            var row = sheet.Row(r);
            if (row.IsEmpty()) continue;
            foreach (var cell in row.CellsUsed())
            {
                var norm = Normalize(cell.GetString());
                if (norm.Contains("isyerino")) colCode = cell.Address.ColumnNumber;
                else if (norm.Contains("iladi")) colCity = cell.Address.ColumnNumber;
            }
            if (colCode > 0 && colCity > 0) { headerRow = r; break; }
            colCode = 0; colCity = 0; // bu satır başlık değilmiş, sıfırla ve devam et
        }
        if (headerRow == 0) return map;

        foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() > headerRow))
        {
            var code = TextNormalizationHelper.NormalizeCode(CellText(row, colCode));
            var city = CellText(row, colCity);
            if (code.Length == 0 || city.Length == 0) continue;
            map[code] = city; // aynı kod tekrar ederse sonuncusu kazanır — kaynak veri tekil olmalı
        }
        return map;
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
            // Bazı dosyalarda (özellikle Excel dışı bir araçla üretilmiş/dışa aktarılmış hakedişlerde)
            // formül hücresinin DataType'ı "Number" görünür ama hesaplanmış (cache'lenmiş) değer hiç
            // yazılmamıştır — ClosedXML böyle bir hücreyi okurken "Specified cast is not valid"
            // (InvalidCastException) fırlatır. Bu durum GİRDİDEKİ BİR BOZUKLUKTUR, kodun hatası değildir;
            // tüm analizi çökertmek yerine bu tek hücreyi "okunamadı" (false) kabul edip devam ediyoruz —
            // bu zaten diğer boş/parse edilemeyen hücrelerle aynı muameledir.
            try
            {
                value = (decimal)cell.GetDouble();
                return true;
            }
            catch (InvalidCastException)
            {
                value = 0;
                // Aşağıdaki metin yoluna düşer — GetString() de genelde boş döner (cache'lenmemiş
                // formül), bu durumda text yolu da false döndürür; sonuç: "değer yok" olarak işlenir.
            }
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
