using ClosedXML.Excel;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;
using System.Globalization;
using System.Text;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Sade (flat) format Excel fiyat listesini okur.
/// Zorunlu kolonlar: Ana Grup, Açıklama, Birim, Malzeme, İşçilik  (Alt Grup kullanılmaz)
/// Kolon başlıkları Türkçe karakter / boşluk / büyük-küçük harf normalleştirmesiyle esnek eşleştirilir.
/// Başlık satırı ilk 20 satırda aranır; en az 3 zorunlu kolon bulunursa kabul edilir.
/// </summary>
public static class GenericPriceExcelParser
{
    // Normalize edilmiş kolon başlığı → canonical kolon adı
    private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.Ordinal)
    {
        // Ana Grup
        ["anagrup"]            = "anagrup",
        ["grup"]               = "anagrup",
        ["baslik"]             = "anagrup",
        ["anabaslik"]          = "anagrup",
        ["anakategori"]        = "anagrup",
        ["kategori"]           = "anagrup",
        // Alt Grup / Alt Kategori
        ["altgrup"]            = "altgrup",
        ["altkategori"]        = "altgrup",
        ["altbaslik"]          = "altgrup",
        ["subcategory"]        = "altgrup",
        // Açıklama
        ["aciklama"]           = "aciklama",
        ["yapilacakisincinsi"] = "aciklama",
        ["isincinsi"]          = "aciklama",
        ["description"]        = "aciklama",
        // Birim
        ["birim"]              = "birim",
        ["brm"]                = "birim",
        ["unit"]               = "birim",
        // Malzeme
        ["malzeme"]            = "malzeme",
        ["malzemetl"]          = "malzeme",
        ["malzemebedeli"]      = "malzeme",
        ["material"]           = "malzeme",
        // İşçilik
        ["iscilik"]            = "iscilik",
        ["isciliktl"]          = "iscilik",
        ["iscilikbedeli"]      = "iscilik",
        ["labor"]              = "iscilik",
        // Birim Fiyat
        ["birimfiyat"]         = "birimfiyat",
        ["birimfiyattl"]       = "birimfiyat",
        ["toplam"]             = "birimfiyat",
        ["unitprice"]          = "birimfiyat",
        // Tip
        ["tip"]                = "tip",
        ["fiyattipi"]          = "tip",
        ["pricetype"]          = "tip",
        // İcmal Açıklaması
        ["icmalaciklamasi"]    = "icmal",
        ["icmal"]              = "icmal",
        // Arama Anahtarı
        ["aramaanahtar"]       = "arama",
        ["aramaanahtari"]      = "arama",
        ["arama"]              = "arama",
        // Kaynak
        ["kaynakdosya"]        = "kaynakdosya",
        ["kaynaksayfa"]        = "kaynaksayfa",
        ["kaynaksatir"]        = "kaynaksatir",
        // Poz No
        ["pozno"]              = "pozno",
        ["poz"]                = "pozno",
        // Aktif
        ["aktif"]              = "aktif",
        ["durum"]              = "aktif",
        ["isactive"]           = "aktif",
        // Not
        ["not"]                = "not",
        ["note"]               = "not",
        ["notes"]              = "not",
        // BVN Liste USD
        ["bvnlisteusd"]        = "listeusd",
        ["listeusd"]           = "listeusd",
        ["listepriceusd"]      = "listeusd",
        ["listfiyatusd"]       = "listeusd",
        ["usdliste"]           = "listeusd",
        ["usdlisteprice"]      = "listeusd",
        ["listefijatusd"]      = "listeusd",
        // İskontolu USD
        ["iskontoluusd"]       = "iskontoluusd",
        ["20iskontoluusd"]     = "iskontoluusd",
        ["iskonto20usd"]       = "iskontoluusd",
        ["discountedprice"]    = "iskontoluusd",
        ["discountedusd"]      = "iskontoluusd",
        ["netusd"]             = "iskontoluusd",
        // İskonto oranı
        ["iskonto"]            = "iskonto",
        ["iskontoyuzdesi"]     = "iskonto",
        ["iskontoorani"]       = "iskonto",
        ["discountrate"]       = "iskonto",
        ["discount"]           = "iskonto",
        // Para birimi
        ["parabirimi"]         = "parabirimi",
        ["doviztipi"]          = "parabirimi",
        ["currency"]           = "parabirimi",
        ["currencycode"]       = "parabirimi",
    };

    // Başlık tespiti için zorunlu canonical kolonlar (en az 3'ü bulunmalı)
    private static readonly HashSet<string> RequiredCanonicals =
        new() { "anagrup", "aciklama", "birim", "malzeme", "iscilik" };
    // ------------------------------------------------------------------ //
    //  GİRİŞ NOKTASI
    // ------------------------------------------------------------------ //
    public static PriceImportPreviewDto Parse(Stream stream)
    {
        var preview = new PriceImportPreviewDto();
        var items = new List<PriceItemDto>();
        var mainCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalRowsRead = 0;

        using var wb = new XLWorkbook(stream);
        preview.SheetCount = wb.Worksheets.Count;

        // "Fiyatlar" sayfası varsa önce onu işle, yoksa tüm sayfaları gez
        var sheets = wb.Worksheets.ToList();
        var fiyatlarSheet = sheets.FirstOrDefault(s =>
            string.Equals(s.Name.Trim(), "Fiyatlar", StringComparison.OrdinalIgnoreCase));
        if (fiyatlarSheet != null)
            sheets = new[] { fiyatlarSheet }.Concat(sheets.Where(s => s != fiyatlarSheet)).ToList();

        foreach (var ws in sheets)
        {
            if (ws.IsEmpty()) continue;

            try
            {
                int countBefore = items.Count;
                ParseSheet(ws, items, preview, mainCats, ref totalRowsRead);
                int added = items.Count - countBefore;
                preview.DebugMessages.Add($"Sayfa '{ws.Name}': {added} kalem okundu.");
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"Sayfa '{ws.Name}': {ex.Message}");
                preview.ErrorCount++;
            }
        }

        preview.TotalRowsRead = totalRowsRead;
        preview.Items = items;
        preview.TotalItems = items.Count(i => i.IsSelectable);
        preview.MainCategoryCount = mainCats.Count;
        preview.SubCategoryCount = 0;
        preview.FixedPriceCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.FixedPrice);
        preview.LaborOnlyCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.LaborOnly);
        preview.MaterialOnlyCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.MaterialOnly);
        preview.VariablePriceCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.VariablePrice);
        preview.PercentageBasedCount = items.Count(i => i.IsSelectable && i.PriceType == PriceType.PercentageBased);
        preview.MissingUnitCount = items.Count(i => i.IsSelectable && i.HasMissingUnit);

        if (preview.TotalItems == 0)
        {
            if (preview.Errors.Count == 0)
                preview.Errors.Add(
                    "Excel içinde fiyat kalemi bulunamadı. Kolon başlıkları tanınamadı. " +
                    "Beklenen kolonlar: Ana Grup, Açıklama, Birim, Malzeme, İşçilik.");
            preview.DebugMessages.Add($"Okunan sayfa sayısı: {preview.SheetCount}");
            preview.DebugMessages.Add($"Okunan toplam satır: {totalRowsRead}");
            if (preview.MatchedColumns.Count > 0)
                preview.DebugMessages.Add("Son eşleşen kolonlar: " + string.Join(", ", preview.MatchedColumns));
        }

        return preview;
    }

    // ------------------------------------------------------------------ //
    //  SAYFA OKUMA
    // ------------------------------------------------------------------ //
    private static void ParseSheet(
        IXLWorksheet ws, List<PriceItemDto> items,
        PriceImportPreviewDto preview, HashSet<string> mainCats,
        ref int totalRowsRead)
    {
        int lastUsedRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        int searchLimit = Math.Min(20, lastUsedRow);

        // Başlık satırını bul — ilk 20 satır, en az 3 zorunlu kolon gerekli
        IXLRow? headerRow = null;
        Dictionary<string, int>? colMap = null;

        for (int r = 1; r <= searchLimit; r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;

            var candidate = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cell in row.CellsUsed())
            {
                var raw = cell.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(raw)) continue;

                var norm = NormalizeHeader(raw);
                if (string.IsNullOrEmpty(norm)) continue;

                // Tam eşleşme
                if (HeaderAliases.TryGetValue(norm, out var canonical))
                {
                    if (!candidate.ContainsKey(canonical))
                        candidate[canonical] = cell.Address.ColumnNumber;
                    continue;
                }

                // Prefix eşleşmesi (örn: "malzemetl123" → "malzeme")
                foreach (var (key, val) in HeaderAliases)
                {
                    if (norm.StartsWith(key, StringComparison.Ordinal) ||
                        key.StartsWith(norm, StringComparison.Ordinal))
                    {
                        if (!candidate.ContainsKey(val))
                            candidate[val] = cell.Address.ColumnNumber;
                        break;
                    }
                }
            }

            int reqFound = candidate.Keys.Count(k => RequiredCanonicals.Contains(k));
            if (reqFound >= 3)
            {
                headerRow = row;
                colMap = candidate;
                preview.HeaderRowNumber = r;
                preview.MatchedColumns = candidate.Keys.ToList();
                preview.DebugMessages.Add(
                    $"Sayfa '{ws.Name}': Başlık satırı {r}. satırda bulundu. " +
                    $"Eşleşen kolonlar: [{string.Join(", ", candidate.Keys)}]");
                break;
            }
        }

        if (headerRow is null || colMap is null)
        {
            preview.Errors.Add(
                $"Sayfa '{ws.Name}': Başlık satırı bulunamadı. " +
                $"İlk {searchLimit} satırda en az 3 zorunlu kolon " +
                "(Ana Grup, Açıklama, Birim, Malzeme, İşçilik) gereklidir.");
            preview.ErrorCount++;
            return;
        }

        // Kolon indisleri
        colMap.TryGetValue("anagrup",     out int colAnaGrup);
        colMap.TryGetValue("altgrup",     out int colAltGrup);
        colMap.TryGetValue("aciklama",    out int colAciklama);
        colMap.TryGetValue("icmal",       out int colIcmal);
        colMap.TryGetValue("birim",       out int colBirim);
        colMap.TryGetValue("malzeme",     out int colMalzeme);
        colMap.TryGetValue("iscilik",     out int colIscilik);
        colMap.TryGetValue("tip",         out int colTip);
        colMap.TryGetValue("arama",       out int colArama);
        colMap.TryGetValue("kaynakdosya", out int colKaynakDosya);
        colMap.TryGetValue("kaynaksayfa", out int colKaynakSayfa);
        colMap.TryGetValue("kaynaksatir", out int colKaynakSatir);
        colMap.TryGetValue("pozno",       out int colPozNo);
        colMap.TryGetValue("aktif",       out int colAktif);
        colMap.TryGetValue("not",         out int colNot);
        // Döviz bazlı kolonlar
        colMap.TryGetValue("listeusd",     out int colListeUsd);
        colMap.TryGetValue("iskontoluusd", out int colIskontoluUsd);
        colMap.TryGetValue("iskonto",      out int colIskonto);
        colMap.TryGetValue("parabirimi",   out int colParaBirimi);

        int startRow = headerRow.RowNumber() + 1;
        int skipped = 0;

        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() >= startRow))
        {
            totalRowsRead++;
            try
            {
                var aciklama = CellText(row, colAciklama);
                var anaGrup  = colAnaGrup > 0 ? CellText(row, colAnaGrup) : string.Empty;
                var altGrup  = colAltGrup > 0 ? CellText(row, colAltGrup) : string.Empty;
                var notMetni = colNot > 0 ? CellText(row, colNot) : string.Empty;

                // Açıklama zorunlu
                if (string.IsNullOrWhiteSpace(aciklama))
                {
                    skipped++;
                    continue;
                }

                // Fiyat parse
                decimal malzeme = 0, iscilik = 0;
                bool hasMalzeme = false, hasIscilik = false;

                if (colMalzeme > 0)
                    hasMalzeme = TryGetDecimal(row.Cell(colMalzeme), out malzeme);
                if (colIscilik > 0)
                    hasIscilik = TryGetDecimal(row.Cell(colIscilik), out iscilik);

                // Döviz bazlı ürün tespiti (BVN Liste USD vb.)
                decimal listePriceUsd = 0, iskontoluUsd = 0, iskontoOran = 0;
                bool hasBvnListeUsd = colListeUsd > 0 &&
                    TryGetDecimalUsd(row.Cell(colListeUsd), out listePriceUsd) &&
                    listePriceUsd > 0;
                if (colIskontoluUsd > 0) TryGetDecimalUsd(row.Cell(colIskontoluUsd), out iskontoluUsd);
                if (colIskonto > 0)      TryGetDecimal(row.Cell(colIskonto), out iskontoOran);
                bool isCurrencyBased = hasBvnListeUsd;
                if (isCurrencyBased)
                {
                    if (iskontoOran == 0) iskontoOran = 20m; // varsayılan BVN %20
                    if (iskontoluUsd == 0) iskontoluUsd = listePriceUsd * (1m - iskontoOran / 100m);
                }
                var currencyCode = isCurrencyBased
                    ? (colParaBirimi > 0 && !string.IsNullOrWhiteSpace(CellText(row, colParaBirimi))
                        ? CellText(row, colParaBirimi).ToUpperInvariant()
                        : "USD")
                    : null;

                var tipTxt    = colTip > 0 ? CellText(row, colTip) : string.Empty;
                var priceType = ParseTip(tipTxt, malzeme, iscilik);
                bool isManuelTip = priceType == PriceType.VariablePrice ||
                                   priceType == PriceType.PercentageBased;

                // Satır kabul: fiyat var VEYA manuel/değişken tip VEYA döviz bazlı
                if (!hasMalzeme && !hasIscilik && !isManuelTip && !isCurrencyBased)
                {
                    skipped++;
                    continue;
                }

                var birim      = colBirim > 0 ? CellText(row, colBirim) : string.Empty;
                bool missingUnit = string.IsNullOrWhiteSpace(birim);
                bool isActive  = colAktif > 0 ? ParseAktif(CellText(row, colAktif)) : true;
                if (missingUnit) isActive = false;

                var icmal      = colIcmal > 0 ? CellText(row, colIcmal) : string.Empty;
                var aramaTxt   = colArama > 0 ? CellText(row, colArama) : string.Empty;
                var pozNo      = colPozNo > 0 ? CellText(row, colPozNo) : string.Empty;
                var kaynakDosya = colKaynakDosya > 0 ? CellText(row, colKaynakDosya) : ws.Name;
                var kaynakSayfa = colKaynakSayfa > 0 ? CellText(row, colKaynakSayfa) : ws.Name;
                int? kaynakSatir = null;
                if (colKaynakSatir > 0 && row.Cell(colKaynakSatir).TryGetValue<int>(out var ks))
                    kaynakSatir = ks;

                if (string.IsNullOrEmpty(icmal))
                    icmal = string.IsNullOrEmpty(anaGrup) ? aciklama : $"{anaGrup} - {aciklama}";

                var dto = new PriceItemDto
                {
                    SourceSheetName    = string.IsNullOrEmpty(kaynakDosya) ? kaynakSayfa : kaynakDosya,
                    SourceRowNumber    = kaynakSatir ?? row.RowNumber(),
                    PozNo              = string.IsNullOrEmpty(pozNo) ? aramaTxt : pozNo,
                    MainCategory       = string.IsNullOrEmpty(anaGrup) ? null : anaGrup,
                    SubCategory        = string.IsNullOrEmpty(altGrup) ? null : altGrup,
                    SubCategory2       = null,
                    Description        = aciklama,
                    DisplayName        = aciklama,
                    InvoiceDescription = icmal,
                    Unit               = birim,
                    MaterialPrice      = malzeme,
                    LaborPrice         = iscilik,
                    PriceType          = priceType,
                    IsSelectable       = true,
                    IsActive           = isActive,
                    IsManuallyAdded    = false,
                    HasMissingUnit     = missingUnit,
                    Notes              = string.IsNullOrEmpty(notMetni) ? null : notMetni,
                    // Döviz bazlı
                    IsCurrencyBased      = isCurrencyBased,
                    CurrencyCode         = currencyCode,
                    ListPriceUsd         = isCurrencyBased ? listePriceUsd : null,
                    DiscountRate         = isCurrencyBased ? iskontoOran : null,
                    DiscountedUsdPrice   = isCurrencyBased ? iskontoluUsd : null,
                    ExchangeRateRequired = isCurrencyBased,
                };

                items.Add(dto);
                if (!string.IsNullOrEmpty(anaGrup)) mainCats.Add(anaGrup);
            }
            catch (Exception ex)
            {
                preview.Errors.Add($"Sayfa '{ws.Name}' satır {row.RowNumber()}: {ex.Message}");
                preview.ErrorCount++;
            }
        }

        if (skipped > 0)
            preview.DebugMessages.Add($"Sayfa '{ws.Name}': {skipped} satır atlandı (boş/fiyatsız).");
    }

    // ------------------------------------------------------------------ //
    //  YARDIMCI METODLAR
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Kolon başlığını normalize eder: trim, lowercase, Türkçe karakter sadeleştir,
    /// nokta / boşluk / parantez / tire / alt çizgi kaldır.
    /// Örnek: "İşçilik (TL)" → "isciliktl"  |  "Brm." → "brm"
    /// </summary>
    public static string NormalizeHeader(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var c in input.Trim())
        {
            var mapped = c switch
            {
                'İ' or 'I' => 'i',
                'ı'        => 'i',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ğ' or 'ğ' => 'g',
                'Ö' or 'ö' => 'o',
                'Ü' or 'ü' => 'u',
                _ when char.IsUpper(c) => char.ToLowerInvariant(c),
                _ => c,
            };
            // Boşluk / nokta / parantez / tire / alt çizgi / eğik çizgi atla
            if (mapped is ' ' or '.' or '(' or ')' or '-' or '_' or '/')
                continue;
            sb.Append(mapped);
        }
        return sb.ToString();
    }

    private static PriceType ParseTip(string tipTxt, decimal malzeme, decimal iscilik)
    {
        if (!string.IsNullOrWhiteSpace(tipTxt))
        {
            var t = NormalizeHeader(tipTxt);
            if (t.Contains("degisken") || t.Contains("variable") || t.Contains("manuel"))
                return PriceType.VariablePrice;
            if (t.Contains("yuzde") || t.Contains("percent"))
                return PriceType.PercentageBased;
            if (t.Contains("iscilik") || t.Contains("labor"))
                return PriceType.LaborOnly;
            if (t.Contains("malzeme") || t.Contains("material"))
                return PriceType.MaterialOnly;
            if (t.Contains("sabit") || t.Contains("fixed"))
                return PriceType.FixedPrice;
        }
        if (malzeme > 0 && iscilik > 0) return PriceType.FixedPrice;
        if (iscilik > 0 && malzeme == 0) return PriceType.LaborOnly;
        if (malzeme > 0 && iscilik == 0) return PriceType.MaterialOnly;
        return PriceType.FixedPrice;
    }

    private static bool ParseAktif(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim().ToLowerInvariant();
        return t is "1" or "true" or "evet" or "yes" or "aktif";
    }

    /// <summary>
    /// Türk Lirası fiyat formatlarını parse eder.
    /// Desteklenen: "1.750,00 TL" | "1750,00" | "1750.00" | "₺1.750,00" | "0" | ""
    /// </summary>
    public static bool ParseDecimalTurkish(string value, out decimal result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim()
            .Replace("TL", "", StringComparison.OrdinalIgnoreCase)
            .Replace("₺", "")
            .Replace(" ", "")
            .Trim();

        if (string.IsNullOrEmpty(s)) return false;

        // Virgül ondalık ayırıcı — Türk formatı: binlik nokta, ondalık virgül ("1.750,00")
        if (s.Contains(','))
        {
            var normalized = s.Replace(".", "").Replace(",", ".");
            if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                return true;
        }

        // Nokta ondalık ayırıcı: "1750.00"
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
            return true;

        // tr-TR ile son deneme
        if (decimal.TryParse(s, NumberStyles.Any, new CultureInfo("tr-TR"), out result))
            return true;

        return false;
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
        var text = cell.GetString()?.Trim() ?? string.Empty;
        return ParseDecimalTurkish(text, out value);
    }

    private static string CellText(IXLRow row, int col)
        => col > 0 ? row.Cell(col).GetString()?.Trim() ?? string.Empty : string.Empty;

    /// <summary>
    /// USD değer okur. "69,00 USD", "69.00$", "$ 69" gibi biçimleri destekler.
    /// </summary>
    private static bool TryGetDecimalUsd(IXLCell cell, out decimal value)
    {
        value = 0;
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number)
        {
            value = (decimal)cell.GetDouble();
            return value > 0;
        }
        var text = cell.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return false;
        // USD sembollerini temizle
        text = text
            .Replace("USD", "", StringComparison.OrdinalIgnoreCase)
            .Replace("$", "")
            .Trim();
        return ParseDecimalTurkish(text, out value);
    }
}
