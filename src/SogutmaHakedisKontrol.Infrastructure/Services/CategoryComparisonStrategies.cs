using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

internal static class ComparisonResultFactory
{
    public static AiComparisonResult New(int jobId, AiDocumentPage page, string storeLabel,
        AiComparisonItemType type, string description, string? formValue, string? hakedisValue,
        AiComparisonStatus status, string explanation, int? checkItemId = null, int? matchedMaterialId = null) => new()
    {
        JobId = jobId,
        StoreId = page.MatchedStoreId,
        StoreLabel = storeLabel,
        VisitDate = page.ServiceDate,
        SourcePageId = page.Id,
        ProgressPaymentCheckItemId = checkItemId,
        MatchedMaterialId = matchedMaterialId,
        ItemType = type,
        Description = description,
        FormValue = formValue,
        HakedisValue = hakedisValue,
        Status = status,
        Explanation = explanation,
        CreatedAt = DateTime.Now,
    };

    /// <summary>Bir sonucun kaynağına (sayfa/hakediş kalemi/kalem türü/açıklama) bağlı kalıcı anahtarı —
    /// kullanıcı onayının (AiComparisonOverride) hangi sonuca ait olduğunu, sonuç her recompute'ta silinip
    /// yeniden üretilse bile tanımak için kullanılır. Tek kaynak — hem AiAnalysisPipelineService'in
    /// override yazma/okuma metodları hem de FormNumberMatcher'ın override-farkında kurtarma mantığı bunu kullanır.</summary>
    public static string ComputeMatchKey(AiComparisonResult r) =>
        $"{r.SourcePageId?.ToString() ?? "-"}|{r.ProgressPaymentCheckItemId?.ToString() ?? "-"}|{r.ItemType}|{r.Description}";

    /// <summary>MatchKey'in ilk iki alanı (sayfa+hakediş kalemi) — ItemType/Description'dan bağımsız,
    /// "aynı kaynağa ait" satırları tanımak için kullanılır (bkz. AiAnalysisPipelineService.ApplyOverridesAsync:
    /// bir gate/mağaza-tarih override'ının, gate açıldıktan sonra üretilen GERÇEK kategori sonucuna da
    /// "Kontrol Edildi" rozetini taşıması).</summary>
    public static string ComputePageItemKey(AiComparisonResult r) =>
        $"{r.SourcePageId?.ToString() ?? "-"}|{r.ProgressPaymentCheckItemId?.ToString() ?? "-"}";

    /// <summary>Kalıcı bir MatchKey dizesinden PageItemKey'i (ilk iki alan) çıkarır.</summary>
    public static string PageItemKeyFromMatchKey(string matchKey)
    {
        var parts = matchKey.Split('|');
        return parts.Length >= 2 ? $"{parts[0]}|{parts[1]}" : matchKey;
    }
}

/// <summary>
/// Tek kalemli miktar kontrollerinde (Glikol/Gaz kg) çok sık görülen bir AI/OCR hatasını tolere eder:
/// el yazısı miktarda ondalık ayırıcı (nokta/virgül) ya YANLIŞLIKLA EKLENİR (20 → 2.0/2) ya da bir basamak
/// ATLANIR (15 → 1.5) — sonuçta okunan değer, gerçek değerin tam olarak 10 katı ya da 1/10'u çıkar. Bu
/// kalıp normal ölçüm sapmasından ayırt edilebilir kadar belirgindir (ör. 2 ile 20 arasındaki fark tolerans
/// içinde asla YAKALANMAZ, yalnızca "×10" ilişkisiyle yakalanır) — bu yüzden yanlış negatif riski düşüktür.
/// GÜVENLİK: hiçbir değer sessizce "düzeltilip" gösterilmez — Durum Uygun'a çevrilir ama formda gerçekte
/// OKUNAN ham değer (glycolFormStr/gasFormStr) olduğu gibi kalır, kullanıcı her zaman tooltip'te "Form: X"
/// olarak gerçek okumayı görür ve gerektiğinde manuel olarak sorgulayabilir.
/// </summary>
internal static class QuantityOcrHelper
{
    public static bool IsDecimalShiftMatch(decimal formValue, decimal hakedisValue, decimal tolerance)
    {
        if (formValue <= 0 || hakedisValue <= 0) return false;
        return Math.Abs(formValue * 10 - hakedisValue) <= tolerance || Math.Abs(formValue / 10 - hakedisValue) <= tolerance;
    }
}

/// <summary>
/// Servis formunu daha önce yüklenmiş hakediş Excel satırlarıyla eşleştirir. Form numarası ANA eşleştirme
/// anahtarıdır — ayrı bir mağaza listesi kullanılmaz; mağaza doğrulaması, form numarasıyla bulunan hakediş
/// satırının kendi mağaza kodu/adı ile servis formundan okunan mağaza bilgisi karşılaştırılarak yapılır.
/// Sıra kesinlikle: 1) form no oku, 2) Excel'de ara, 3) mağazayı doğrula, 4) tarihi doğrula — bu dört adım
/// geçilmeden kategoriye özel kontrol asla çalıştırılmaz.
/// </summary>
internal static class FormNumberMatcher
{
    private const decimal MinFormNumberConfidence = 0.4m;
    private const double MinStoreNameSimilarity = 0.5;

    // Mağaza adı karşılaştırmasında anlamsız gürültü sayılan, karar üzerinde etkisi olmaması gereken
    // kelimeler (zincir/format ekleri, il adı, adres bağlaçları) — yalnızca mağaza eşleştirmede kullanılır,
    // TextNormalizationHelper.NormalizeName'e eklenmez çünkü malzeme adı gibi başka karşılaştırmaları bozar.
    private static readonly HashSet<string> StoreNameNoiseWords = new()
    {
        "mm", "mmm", "migros", "mah", "mahallesi", "sk", "sok", "ankara",
    };

    /// <summary>
    /// Eski/genel kullanım: Adım 3/4'te (mağaza/tarih) bir sorun varsa TEK bir hata sonucu döner ve
    /// Matched null olur — kategori kontrolü hiç çalışmaz (Varsayılan/Gaz/İlave İşler stratejileri için
    /// hâlâ geçerli davranış). Glikol Kullanım artık bunun yerine <see cref="MatchWithSoftIssue"/> kullanır
    /// (bkz. o metodun açıklaması) — mağaza/tarih sorunu olsa bile miktar karşılaştırmasının aynı satırda
    /// bağımsız çalışabilmesi için. overriddenMatchKeys parametresi artık kullanılmıyor (bkz. AŞAMA 2 —
    /// manuel onay kontrol tipini asla değiştiremez); imza geriye dönük uyumluluk için korunmuştur.
    /// </summary>
    public static (List<ProgressPaymentCheckItem>? Matched, AiComparisonResult? Error) Match(
        int jobId, AiDocumentPage page, List<ProgressPaymentCheckItem> checkItems, HashSet<string> overriddenMatchKeys)
    {
        var (matched, hardError, softIssue) = MatchCore(jobId, page, checkItems);
        if (hardError != null) return (null, hardError);
        if (softIssue != null) return (null, softIssue);
        return (matched, null);
    }

    /// <summary>
    /// Glikol/Gaz gibi tek kalemli kategoriler için: mağaza/tarih uyuşmazlığı (soft issue) bulunsa bile
    /// eşleşen hakediş kalemi grubunu (Matched) DÖNDÜRMEYE DEVAM EDER — böylece çağıran strateji hem
    /// soft issue'yu raporlayabilir hem de asıl miktar karşılaştırmasını aynı ziyaret için hesaplayıp
    /// AYNI SATIRA (bkz. AiComparisonResult.SecondaryFormValue/HakedisValue/Status) ekleyebilir. Yalnızca
    /// gerçekten eşleşen bir aday olmadığında (form no okunamadı/Excel'de yok/mükerrer — HardError) Matched
    /// null döner.
    /// </summary>
    public static (List<ProgressPaymentCheckItem>? Matched, AiComparisonResult? HardError, AiComparisonResult? SoftIssue) MatchWithSoftIssue(
        int jobId, AiDocumentPage page, List<ProgressPaymentCheckItem> checkItems) => MatchCore(jobId, page, checkItems);

    private static (List<ProgressPaymentCheckItem>? Matched, AiComparisonResult? HardError, AiComparisonResult? SoftIssue) MatchCore(
        int jobId, AiDocumentPage page, List<ProgressPaymentCheckItem> checkItems)
    {
        var label = StoreLabelFallback(page);

        // 1) Form numarası okunabildi mi? Düşük güvenle rastgele eşleştirme YAPILMAZ.
        var formNo = TextNormalizationHelper.NormalizeCode(page.FormNumber ?? string.Empty);
        var lowConfidence = page.FormNumberConfidence.HasValue && page.FormNumberConfidence.Value < MinFormNumberConfidence;
        if (string.IsNullOrEmpty(formNo) || lowConfidence)
        {
            var fallback = TryMatchByStoreAndDate(page, checkItems);
            if (fallback != null) return FallbackSoftIssue(jobId, page, fallback, "okunamadı");

            return (null, ComparisonResultFactory.New(jobId, page, label, AiComparisonItemType.Material,
                "Form Numarası Okunamadı", page.FormNumber, null, AiComparisonStatus.ManuelKontrol,
                "Servis formu numarası okunamadığı için hakediş Excelindeki ilgili kayıt güvenilir şekilde tespit edilemedi."), null);
        }

        // 2) Form numarasını hakediş Excelinde ara (MaintenanceFormNo — import sırasında Excel'den okunmuştu).
        var candidates = checkItems
            .Where(i => !string.IsNullOrWhiteSpace(i.MaintenanceFormNo)
                        && TextNormalizationHelper.NormalizeCode(i.MaintenanceFormNo) == formNo)
            .ToList();

        if (candidates.Count == 0)
        {
            var fallback = TryMatchByStoreAndDate(page, checkItems);
            if (fallback != null) return FallbackSoftIssue(jobId, page, fallback, $"\"{page.FormNumber}\" okundu ama hakedişte bulunamadı");

            return (null, ComparisonResultFactory.New(jobId, page, label, AiComparisonItemType.Material,
                "Form Hakedişte Bulunamadı", page.FormNumber, "—", AiComparisonStatus.Eksik,
                $"\"{page.FormNumber}\" numaralı servis formunun yüklenen hakediş Excelinde karşılığı bulunamadı."), null);
        }

        // Aynı form numarasına ait satırlar farklı mağaza/tarihe dağılıyorsa mükerrer numaralandırma var demektir.
        var visitGroups = candidates
            .GroupBy(i => (Store: TextNormalizationHelper.NormalizeCode(i.StoreCode ?? i.StoreName ?? string.Empty), Date: i.VisitDate?.Date))
            .ToList();
        if (visitGroups.Count > 1)
        {
            return (null, ComparisonResultFactory.New(jobId, page, label, AiComparisonItemType.Material,
                "Mükerrer Form Numarası", page.FormNumber, null, AiComparisonStatus.ManuelKontrol,
                $"\"{page.FormNumber}\" numaralı form hakedişte birden fazla farklı mağaza/tarihe ait kayıtla eşleşiyor — manuel kontrol edilmelidir."), null);
        }

        var group = visitGroups[0].ToList();
        var first = group[0];
        var hakedisStoreLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";

        // 3) Mağaza doğrulama — kod varsa öncelikli/kesin kriter, yoksa isim benzerliğine (sınırlı) düşülür.
        var storeCheck = CompareStore(page, first);
        if (storeCheck == StoreCheck.Mismatch)
        {
            var softIssue = ComparisonResultFactory.New(jobId, page, hakedisStoreLabel, AiComparisonItemType.Material,
                "Mağaza Uyuşmazlığı", page.StoreCodeRaw ?? page.StoreNameRaw, hakedisStoreLabel, AiComparisonStatus.UygunDegil,
                $"\"{page.FormNumber}\" numaralı form üzerindeki mağaza {(page.StoreCodeRaw ?? page.StoreNameRaw)} olarak okunmuştur " +
                $"ancak hakediş Excelindeki aynı form numarası farklı mağazaya ({hakedisStoreLabel}) aittir.", first.Id);
            return (group, null, softIssue);
        }
        if (storeCheck == StoreCheck.Inconclusive)
        {
            var softIssue = ComparisonResultFactory.New(jobId, page, hakedisStoreLabel, AiComparisonItemType.Material,
                "Mağaza Doğrulanamadı", page.StoreCodeRaw ?? page.StoreNameRaw, hakedisStoreLabel, AiComparisonStatus.ManuelKontrol,
                $"\"{page.FormNumber}\" numaralı formdaki mağaza bilgisi hakediş kaydıyla yeterli güvenle karşılaştırılamadı — manuel kontrol edilmelidir.", first.Id);
            return (group, null, softIssue);
        }

        // 4) Tarih doğrulama — ikisi de doluyken farklıysa hata; biri boşsa (yetersiz veri) engelleme.
        if (page.ServiceDate.HasValue && first.VisitDate.HasValue && page.ServiceDate.Value.Date != first.VisitDate.Value.Date)
        {
            var softIssue = ComparisonResultFactory.New(jobId, page, hakedisStoreLabel, AiComparisonItemType.Material,
                "Tarih Uyuşmazlığı", page.ServiceDate.Value.ToString("dd.MM.yyyy"), first.VisitDate.Value.ToString("dd.MM.yyyy"),
                AiComparisonStatus.UygunDegil,
                $"Servis formu tarihi {page.ServiceDate:dd.MM.yyyy}, hakediş Excelindeki tarih {first.VisitDate:dd.MM.yyyy} olarak görülmektedir.", first.Id);
            return (group, null, softIssue);
        }

        return (group, null, null);
    }

    /// <summary>
    /// Form numarası hiç okunamadığında/hakedişte bulunamadığında son çare: mağaza kodu/adından
    /// eşleştirmeyi dener. Mağaza adı karşılaştırması zaten "Migros/MM/MMM/Mahallesi/Ankara" gibi ortak,
    /// ayırt edici olmayan kelimeleri atıp yalnızca asıl mağazayı belirleyen kelimelere (ör. "Kahramankazan",
    /// "Yukarı Dikmen") bakar (bkz. NormalizeStoreNameCore/StoreNameNoiseWords). GÜVENLİK ÖNCELİKLİDİR —
    /// finansal mutabakat söz konusu olduğu için belirsiz durumda ASLA tahmin etmez: birden fazla mağaza
    /// adayı varsa ve tarih de ayırt etmiyorsa null döner (çağıran taraf orijinal "bulunamadı" hatasına düşer).
    /// </summary>
    private static List<ProgressPaymentCheckItem>? TryMatchByStoreAndDate(AiDocumentPage page, List<ProgressPaymentCheckItem> checkItems)
    {
        if (string.IsNullOrWhiteSpace(page.StoreCodeRaw) && string.IsNullOrWhiteSpace(page.StoreNameRaw))
            return null; // hiç mağaza ipucu yoksa yedek eşleştirme de imkansızdır

        var visitGroups = checkItems
            .Where(i => !string.IsNullOrWhiteSpace(i.MaintenanceFormNo))
            .GroupBy(i => (Store: TextNormalizationHelper.NormalizeCode(i.StoreCode ?? i.StoreName ?? string.Empty), Date: i.VisitDate?.Date))
            .ToList();

        var formCode = TextNormalizationHelper.NormalizeCode(page.StoreCodeRaw ?? string.Empty);
        var formNameCore = NormalizeStoreNameCore(page.StoreNameRaw);

        bool IsStoreMatch(ProgressPaymentCheckItem item)
        {
            var itemCode = TextNormalizationHelper.NormalizeCode(item.StoreCode ?? string.Empty);
            if (!string.IsNullOrEmpty(formCode) && !string.IsNullOrEmpty(itemCode) && formCode == itemCode)
                return true; // kod eşleşiyor — en güçlü sinyal

            // Kod hiç yoksa YA DA varsa ama eşleşmiyorsa (el yazısı/damga OCR hatası çok yaygındır —
            // bkz. CompareStore'daki aynı tolerans) isim benzerliğine düş. Kod uyuşmazlığını TEK BAŞINA
            // reddetme sebebi sayma; asıl karar verici, ortak kelimeler (Migros/MM/Mahallesi/Ankara)
            // atılmış isim benzerliğidir.
            var itemNameCore = NormalizeStoreNameCore(item.StoreName);
            if (string.IsNullOrEmpty(formNameCore) || string.IsNullOrEmpty(itemNameCore)) return false;
            return StoreNameSimilarity(formNameCore, itemNameCore) >= MinStoreNameSimilarity;
        }

        var storeMatches = visitGroups.Where(g => IsStoreMatch(g.First())).ToList();
        if (storeMatches.Count == 0) return null;

        // Aynı mağazaya ait birden fazla ziyaret/aday varsa, tarih ile daraltmayı dene.
        if (storeMatches.Count > 1 && page.ServiceDate.HasValue)
            storeMatches = storeMatches.Where(g => g.Key.Date == page.ServiceDate.Value.Date).ToList();

        // Hâlâ birden fazla ya da hiç aday yoksa güvenli davran — tahmin etme.
        return storeMatches.Count == 1 ? storeMatches[0].ToList() : null;
    }

    private static (List<ProgressPaymentCheckItem>? Matched, AiComparisonResult? HardError, AiComparisonResult? SoftIssue) FallbackSoftIssue(
        int jobId, AiDocumentPage page, List<ProgressPaymentCheckItem> group, string formNoSorunu)
    {
        var first = group[0];
        var hakedisStoreLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";
        var softIssue = ComparisonResultFactory.New(jobId, page, hakedisStoreLabel, AiComparisonItemType.Material,
            "Form No Yerine Mağazadan Eşleşti", page.FormNumber, first.MaintenanceFormNo, AiComparisonStatus.ManuelKontrol,
            $"Form numarası {formNoSorunu} — bunun yerine mağaza bilgisi ({page.StoreCodeRaw ?? page.StoreNameRaw}) " +
            $"kullanılarak \"{hakedisStoreLabel}\" / \"{first.MaintenanceFormNo}\" numaralı hakediş kaydıyla eşleştirildi. " +
            "Lütfen doğruluğunu kontrol edin.", first.Id);
        return (group, null, softIssue);
    }

    private enum StoreCheck { Matched, Mismatch, Inconclusive }

    /// <summary>
    /// Amaç OCR'ın mağaza kodunu birebir doğru okumasını beklemek DEĞİLDİR — form numarasıyla bulunan
    /// hakediş kaydının, formdaki mağaza bilgileriyle makul şekilde aynı mağazaya ait olup olmadığını
    /// belirlemektir. El yazısı mağaza kodu OCR hatası (ör. 7956→7856) tek başına uyuşmazlık sebebi
    /// sayılmaz — mağaza adı yeterli benzerlikteyse (kısaltılmış/eksik yazılmış olsa bile) eşleşme kabul
    /// edilir. Karar tablosu (Durum 1-6):
    ///  1) Kod eşleşiyor → Eşleşti (isim kısaltılmış/eksik olsa da önemsiz).
    ///  2/5) Kod farklı/okunamıyor ama isim benzerliği ≥ %50 → Eşleşti (kod OCR hatası varsayılır).
    ///  4) Her iki kod da mevcut ve farklı, isimler de karşılaştırılabilir ve belirgin şekilde farklı → Uyuşmazlık.
    ///  6) Ne kod (güvenilir eşleşme) ne isim (yeterli benzerlik) doğrulanabiliyor → Manuel Kontrol.
    /// </summary>
    private static StoreCheck CompareStore(AiDocumentPage page, ProgressPaymentCheckItem item)
    {
        var formCode = TextNormalizationHelper.NormalizeCode(page.StoreCodeRaw ?? string.Empty);
        var itemCode = TextNormalizationHelper.NormalizeCode(item.StoreCode ?? string.Empty);
        if (!string.IsNullOrEmpty(formCode) && !string.IsNullOrEmpty(itemCode) && formCode == itemCode)
            return StoreCheck.Matched; // Durum 1/3

        var formNameCore = NormalizeStoreNameCore(page.StoreNameRaw);
        var itemNameCore = NormalizeStoreNameCore(item.StoreName);
        var namesComparable = !string.IsNullOrEmpty(formNameCore) && !string.IsNullOrEmpty(itemNameCore);

        if (namesComparable && StoreNameSimilarity(formNameCore, itemNameCore) >= MinStoreNameSimilarity)
        {
            // Kod farklıysa (ör. 7956→7856) burada sessizce görmezden geliniyor — iç log niteliğinde:
            // "Mağaza kodu OCR sonucu farklı ancak mağaza adı ve form numarası eşleşti." Kullanıcıya
            // hata olarak GÖSTERİLMEZ (Match başarıyla döner, hiçbir AiComparisonResult üretilmez).
            return StoreCheck.Matched; // Durum 2/5
        }

        var codesBothPresentAndDifferent = !string.IsNullOrEmpty(formCode) && !string.IsNullOrEmpty(itemCode);
        if (codesBothPresentAndDifferent && namesComparable)
            return StoreCheck.Mismatch; // Durum 4 — kod da isim de belirgin şekilde farklı

        return StoreCheck.Inconclusive; // Durum 6 — ne kod ne isim güvenilir şekilde doğrulanabiliyor
    }

    /// <summary>
    /// Mağaza adı benzerliği — SIRA BAĞIMSIZ karşılaştırır: formdaki serbest metin (ör. "İŞİN YERİ"
    /// alanı) ile Excel'deki resmi mağaza adının kelime sırası farklı olabilir (ör. form "AKSARAY PARK
    /// SİTE MİGROS", Excel "PARK SİTE AKSARAY MM MİGROS") — bu durum kısaltılmış adlarda olduğu gibi
    /// hata sayılmamalıdır. Önce tam içerme (kısaltılmış ad), sonra ortak kelime oranı (sıra bağımsız),
    /// en son Levenshtein tabanlı karakter oranı (yazım/OCR farklılıkları için) denenir — en yükseği alınır.
    /// </summary>
    private static double StoreNameSimilarity(string coreA, string coreB)
    {
        if (coreA == coreB) return 1.0;
        if (coreA.Contains(coreB) || coreB.Contains(coreA)) return 1.0;

        var tokensA = coreA.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tokensB = coreB.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tokenOverlap = 0.0;
        if (tokensA.Count > 0 && tokensB.Count > 0)
        {
            var shared = tokensA.Intersect(tokensB).Count();
            var smaller = Math.Min(tokensA.Count, tokensB.Count); // kısa tarafın tüm kelimeleri karşılıkta varsa tam eşleşme sayılır
            tokenOverlap = (double)shared / smaller;
        }

        return Math.Max(tokenOverlap, TextNormalizationHelper.SimilarityRatio(coreA, coreB));
    }

    /// <summary>Mağaza adını normalize edip zincir/format/adres gürültü kelimelerini (MM, MİGROS, MAH.,
    /// SK., ANKARA vb.) atarak yalnızca mağazayı belirleyen esas kelimeleri (ör. "YUKARI DİKMEN",
    /// "SİNCAN") bırakır. Yalnızca mağaza eşleştirmede kullanılır.</summary>
    private static string NormalizeStoreNameCore(string? name)
    {
        var normalized = TextNormalizationHelper.NormalizeName(name);
        if (string.IsNullOrEmpty(normalized)) return string.Empty;
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !StoreNameNoiseWords.Contains(t));
        return string.Join(' ', tokens);
    }

    private static string StoreLabelFallback(AiDocumentPage page) => page.StoreNameRaw ?? page.StoreCodeRaw ?? "Bilinmeyen Mağaza";

    /// <summary>Bu sınıfın ürettiği 5 hata türünün Description etiketleri — tek kaynak, hem detay
    /// tablosunun bu satırları filtrelemesi (bkz. FormKontrol.razor) hem de mutabakat özetinin (bkz.
    /// FormReconciliationBuilder) bunları okuması için kullanılır.</summary>
    public static readonly string[] GateErrorDescriptions =
    {
        "Form Numarası Okunamadı", "Form Hakedişte Bulunamadı", "Mükerrer Form Numarası",
        "Mağaza Uyuşmazlığı", "Mağaza Doğrulanamadı", "Tarih Uyuşmazlığı",
    };

    /// <summary>Bunlardan hiçbirinin bağlı olduğu belirli bir hakediş satırı yoktur (checkItemId=null) —
    /// bu yüzden detay tablosunda gösterilmez, yalnızca üst mutabakat özetinde bilgi amaçlı yer alır.</summary>
    public static readonly string[] PageOnlyErrorDescriptions =
    {
        "Form Numarası Okunamadı", "Form Hakedişte Bulunamadı", "Mükerrer Form Numarası",
    };
}

/// <summary>
/// Genel/kategori-bağımsız karşılaştırma — malzeme fuzzy eşleşmesi, adam-saat ve servis ücreti
/// kontrolü. Kategori bazlı özel strateji tanımlanmamış her hakediş türü bunu kullanır
/// (Kompresör, Glikol, Evap, Kısmi Tadilat, İzleme, Periyodik Bakım, kategori seçilmemiş eski kayıtlar —
/// İlave İşler kendi <see cref="AdditionalWorkComparisonStrategy"/>'sini kullanır).
/// Eşleştirme FormNumberMatcher ile form numarası üzerinden yapılır (bkz. sınıf üstü açıklama).
///
/// KRİTİK KURAL — REFERANS HAKEDİŞ EXCELİDİR: firma parayı Excel'deki kalemler üzerinden talep eder,
/// kontrol yönü daima EXCEL → FORM'dur. Her hakediş kalemi (malzeme, adam-saat, servis ücreti) formda
/// aranır; formda bulunup hakedişte talep edilmemiş kalemler (malzeme/adam-saat/servis ücreti fark
/// etmez) kontrol dışıdır — hiçbir sonuç, uyarı veya export notu üretilmez. Ters yönde (form → excel)
/// kontrol YAPILMAZ.
/// </summary>
public class DefaultCategoryComparisonStrategy : ICategoryComparisonStrategy
{
    private const decimal ManHoursTolerance = 0.1m;
    private const decimal MaterialQuantityTolerance = 0.01m;

    private readonly AppDbContext _db;
    public DefaultCategoryComparisonStrategy(AppDbContext db) => _db = db;

    public HakedisCategory? Category => null;
    public string? SingleItemLabel => null; // çok kalemli (malzeme listesi) kategori — tekil etiket yok

    public async Task BuildAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AiComparisonResults.Where(r => r.JobId == job.Id).ToListAsync(cancellationToken);
        _db.AiComparisonResults.RemoveRange(existing);

        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == job.Id && p.DocumentType == AiDocumentType.ServiceForm)
            .Include(p => p.Materials)
            .ToListAsync(cancellationToken);

        var checkItems = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        // Kullanıcının daha önce onayladığı (Uygun'a çevirdiği) sonuçlar — mağaza/tarih uyuşmazlığı
        // onaylanmışsa FormNumberMatcher bunu eşleşti sayıp kategori kontrolünün çalışmasına izin verir.
        var overriddenKeys = (await _db.AiComparisonOverrides
            .Where(o => o.JobId == job.Id)
            .Select(o => o.MatchKey)
            .ToListAsync(cancellationToken)).ToHashSet();

        var results = new List<AiComparisonResult>();

        foreach (var page in pages)
        {
            var (sameVisit, error) = FormNumberMatcher.Match(job.Id, page, checkItems, overriddenKeys);
            if (error != null) { results.Add(error); continue; }

            var first = sameVisit![0];
            var storeLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";

            // ── Malzeme: hakediş → form (EXCEL REFERANSTIR — firma parayı Excel'deki kalemler
            // üzerinden talep eder; her hakediş kalemi formda arınır. Formda olup Excel'de talep
            // edilmemiş malzemeler kontrol dışıdır, hiçbir sonuç üretilmez.) ──
            foreach (var item in sameVisit.Where(i => !i.IsServiceItem))
            {
                var searchName = TextNormalizationHelper.NormalizeName(item.OriginalMaterialName);
                var candidate = page.Materials
                    .Select(m => (Mat: m, Score: TextNormalizationHelper.SimilarityRatio(searchName, TextNormalizationHelper.NormalizeName(m.NormalizedName ?? m.RawName))))
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                var hakedisStr = $"{item.Quantity:0.##} {item.Unit}";

                if (candidate.Mat != null && candidate.Score >= 0.6)
                {
                    var effectiveQty = candidate.Mat.UserCorrectedQuantity ?? candidate.Mat.Quantity;
                    var formStr = effectiveQty.HasValue ? $"{effectiveQty.Value:0.##} {candidate.Mat.UserCorrectedUnit ?? candidate.Mat.Unit}" : "Okunamadı";

                    if (!effectiveQty.HasValue || candidate.Mat.RequiresManualReview)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.ManuelKontrol,
                            $"\"{item.OriginalMaterialName}\" için servis formunda okunan miktar belirsiz — manuel kontrol edilmeli.", item.Id, candidate.Mat.Id));
                    }
                    else if (Math.Abs(effectiveQty.Value - item.Quantity) <= MaterialQuantityTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Hakedişte talep edilen miktar servis formunda doğrulanmıştır.", item.Id, candidate.Mat.Id));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Hakedişte {hakedisStr} talep edilmiş, servis formunda {formStr} doğrulanmıştır.", item.Id, candidate.Mat.Id));
                    }
                }
                else
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                        "—", hakedisStr, AiComparisonStatus.Eksik,
                        $"Hakedişte talep edilen \"{item.OriginalMaterialName}\" servis formunda doğrulanamadı.", item.Id));
                }
            }

            // ── Adam-saat — yalnızca hakedişte adam-saat kalemi TALEP EDİLMİŞSE kontrol edilir
            // (Excel referanstır: formda personel/saat bilgisi olması tek başına bir talep oluşturmaz) ──
            var hakedisManHoursItems = sameVisit
                .Where(i => i.IsServiceItem && TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("adamsaat"))
                .ToList();
            if (hakedisManHoursItems.Count > 0)
            {
                var hakedisManHours = hakedisManHoursItems.Sum(i => i.Quantity);
                if (page.PayableManHours.HasValue)
                {
                    var formStr = $"{page.PayableManHours.Value:0.##} saat";
                    var hakedisStr = $"{hakedisManHours:0.##} saat";
                    if (Math.Abs(page.PayableManHours.Value - hakedisManHours) <= ManHoursTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Formdaki çalışma sürelerine göre hesaplanan adam-saat hakedişle uyumlu."));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Formdaki çalışma sürelerine göre toplam {page.CalculatedManHours:0.##} adam-saat oluşmaktadır. " +
                            $"Kural gereği 4 saat düşülerek en fazla {page.PayableManHours:0.##} adam-saat ödenebilir."));
                    }
                }
                else
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                        "Okunamadı", null, AiComparisonStatus.ManuelKontrol,
                        "Hakedişte adam-saat kalemi var ancak servis formunda personel/çalışma saati bilgisi bulunamadı — tahmin edilmedi."));
                }
            }

            // ── Servis ücreti (şehiriçi/şehirdışı) — yalnızca hakedişte TALEP EDİLMİŞSE kontrol edilir ──
            var serviceFeeItems = sameVisit.Where(i => i.IsServiceItem &&
                (TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("sehirici") ||
                 TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("sehirdisi"))).ToList();

            if (page.ServiceFeeRejectedDueToMaintenance)
            {
                foreach (var fee in serviceFeeItems)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                        "Periyodik bakım mevcut", $"{fee.Quantity:0.##} adet", AiComparisonStatus.UygunDegil,
                        $"{page.ServiceDate:dd.MM.yyyy} tarihinde bu mağazada periyodik bakım bulunmaktadır. " +
                        "Aynı tarih için ayrıca şehir içi/şehir dışı servis ücreti ödenemez.", fee.Id));
                }
            }
            else if (serviceFeeItems.Count > 0)
            {
                foreach (var fee in serviceFeeItems)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                        "Servis ziyareti mevcut", $"{fee.Quantity:0.##} adet", AiComparisonStatus.Uygun,
                        "Periyodik bakım çakışması yok, servis ücreti uygundur."));
                }
            }
        }

        _db.AiComparisonResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// GAZ KULLANIM HAKEDİŞİ için özel karşılaştırma: hakedişteki gaz kg'ı servis formundan çıkarılan
/// gaz kg'ı ile karşılaştırılır. Kaçak yeri/tespiti/onarımı gibi bilgiler (varsa) manuel kontrol
/// notuna eklenir — bu alanlar için henüz sayısal bir kural yok (iskelet aşaması).
/// Eşleştirme FormNumberMatcher ile form numarası üzerinden yapılır.
/// </summary>
public class GasUsageComparisonStrategy : ICategoryComparisonStrategy
{
    private const decimal GasQuantityTolerance = 0.01m;
    private const int RepeatVisitMaxDays = 4;
    private static readonly Regex GasKgRegex = new(@"(\d+(?:[.,]\d+)?)\s*kg", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] LeakKeywords = { "kacak" }; // TextNormalizationHelper "kaçak"→"kacak" çevirir

    // Servis formlarında gaz malzemesi genellikle "gaz" kelimesiyle değil doğrudan soğutucu akışkan
    // koduyla yazılıyor (ör. "R404 A Soğutucu Akışkan", "R410a") — yalnızca "gaz" araması bu satırları
    // kaçırıp gerçek bir miktar varken bile "okunamadı" (Manuel Kontrol) sonucu üretiyordu.
    private static readonly string[] GasKeywords =
        { "gaz", "sogutucu akiskan", "r404", "r410", "r407", "r422", "r134", "r22", "freon" };

    private static bool IsGas(string? materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return false;
        var norm = TextNormalizationHelper.NormalizeName(materialName);
        return GasKeywords.Any(k => norm.Contains(k));
    }

    /// <summary>AI'nın bir servis formu satırına verdiği NormalizedName her zaman güvenilir değildir —
    /// gerçek bir olayda "Yol" (200 km yol masrafı) adlı bir satır NormalizedName="gaz" olarak
    /// etiketlenmiş ve gaz miktarı olarak 200 kg okunmuştu. Kullanıcı talebi net: yalnızca formda
    /// GERÇEKTEN "404" veya "gaz" ifadesi GEÇEN satırlar sayılmalı — bu yüzden burada NormalizedName
    /// yalnızca RawName tamamen boşsa (nadir bir veri eksikliği) yedek olarak kullanılır, RawName varken
    /// AI'nın (yanlış olabilecek) sınıflandırması asla RawName'in üstüne çıkamaz.</summary>
    private static bool IsGasMaterial(AiPageMaterial m) =>
        IsGas(!string.IsNullOrWhiteSpace(m.RawName) ? m.RawName : m.NormalizedName);

    private readonly AppDbContext _db;
    public GasUsageComparisonStrategy(AppDbContext db) => _db = db;

    public HakedisCategory? Category => HakedisCategory.GasUsage;
    public string? SingleItemLabel => "Gaz Miktarı (kg)";

    public async Task BuildAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AiComparisonResults.Where(r => r.JobId == job.Id).ToListAsync(cancellationToken);
        _db.AiComparisonResults.RemoveRange(existing);

        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == job.Id && p.DocumentType == AiDocumentType.ServiceForm)
            .Include(p => p.Materials)
            .ToListAsync(cancellationToken);

        var checkItems = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        var results = new List<AiComparisonResult>();

        // Hangi hakediş kaleminin hangi sayfayla eşleştiği burada toplanır — Tekrar Ziyaret Uyarısı
        // (bkz. AddRepeatVisitWarnings) sayfa eşleşmesinden bağımsız üretiliyor olsa da, "Formu Göster"
        // butonunun görünebilmesi için satırın SourcePageId'ye ihtiyacı var (bkz. FormKontrol.razor).
        var checkItemPageId = new Dictionary<int, int>();

        foreach (var page in pages)
        {
            // MatchWithSoftIssue: mağaza/tarih uyuşmazlığı (softIssue) olsa bile eşleşen grup (sameVisit)
            // döner — böylece gaz miktarı BAĞIMSIZ olarak hesaplanıp AYNI SATIRA eklenebilir (bkz. Glikol
            // ile aynı desen — GlycolUsageComparisonStrategy). Yalnızca gerçekten eşleşen bir kayıt yoksa
            // (hardError — form no okunamadı/Excel'de yok/mükerrer) hiçbir hesaplama yapılamaz.
            var (sameVisit, hardError, softIssue) = FormNumberMatcher.MatchWithSoftIssue(job.Id, page, checkItems);
            if (hardError != null) { results.Add(hardError); continue; }

            foreach (var item in sameVisit!) checkItemPageId.TryAdd(item.Id, page.Id);

            var first = sameVisit![0];
            var storeLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";

            // Excel referanstır: hakedişte gaz kalemi TALEP EDİLMEMİŞSE, formda gaz kullanımından
            // bahsedilmesi tek başına bir talep oluşturmaz — hiçbir sonuç üretilmez. Soft issue varsa
            // (mağaza/tarih) yine de kendi başına raporlanır.
            var hakedisGasItems = sameVisit.Where(i => IsGas(i.OriginalMaterialName)).ToList();
            if (hakedisGasItems.Count > 0)
            {
                var (formGasKg, gasRawBeforeCorrection) = ExtractGasKg(page);
                var hakedisGasKg = hakedisGasItems.Sum(i => i.Quantity);
                var firstGasItemId = hakedisGasItems[0].Id;

                AiComparisonStatus gasStatus;
                string gasFormStr, gasExplanation;
                var hakedisStr = $"{hakedisGasKg:0.##} kg";
                // Tüpler 5 kg'dan başlar — AI'nın okuduğu değer bu yüzden otomatik düzeltilmiş olabilir
                // (bkz. NormalizeGasReading). Düzeltme yapıldıysa açıklamada her zaman belirtilir, sonuç
                // Uygun ya da Uygun Değil çıksa da fark etmez — kullanıcı "1,5 kg" yazsa bile bunun
                // aslında "15 kg" okunduğunu bilmeli.
                var correctionNote = gasRawBeforeCorrection.HasValue
                    ? $" (AI formda {gasRawBeforeCorrection.Value:0.##} kg okudu; gaz tüpleri 5 kg'nin altında olamayacağından {formGasKg:0.##} kg olarak düzeltildi.)"
                    : string.Empty;

                if (!formGasKg.HasValue)
                {
                    gasStatus = AiComparisonStatus.ManuelKontrol;
                    gasFormStr = "Okunamadı";
                    gasExplanation = "Hakedişte gaz kalemi var ancak servis formunda gaz kg bilgisi açıkça bulunamadı — manuel kontrol edilmeli.";
                }
                else
                {
                    gasFormStr = $"{formGasKg.Value:0.##} kg";
                    if (Math.Abs(formGasKg.Value - hakedisGasKg) <= GasQuantityTolerance)
                    {
                        gasStatus = AiComparisonStatus.Uygun;
                        gasExplanation = "Hakedişteki gaz miktarı servis formuyla uyumlu." + correctionNote;
                    }
                    else if (QuantityOcrHelper.IsDecimalShiftMatch(formGasKg.Value, hakedisGasKg, GasQuantityTolerance))
                    {
                        // OCR'ın ondalık ayırıcıyı yanlış eklediği/bir basamak atladığı çok yaygın bir
                        // hata (20→2, 15→1.5) — gerçek okuma tooltip'te görünür kalır, yalnızca Durum
                        // düzeltilir (bkz. Glikol ile aynı desen).
                        gasStatus = AiComparisonStatus.Uygun;
                        gasExplanation = $"Servis formunda {gasFormStr} okunmuştur — muhtemel ondalık basamak okuma hatası nedeniyle hakedişteki {hakedisStr} ile eşleştirilmiş ve uygun kabul edilmiştir.{correctionNote}";
                    }
                    else
                    {
                        gasStatus = AiComparisonStatus.UygunDegil;
                        gasExplanation = $"Hakedişte {hakedisStr} gaz belirtilmiş, servis formunda {gasFormStr} tespit edilmiştir.{correctionNote}";
                    }
                }

                if (softIssue != null)
                {
                    // Satırın ANA konusu (Description/Status/Durum) mağaza/tarih sorunudur — gaz miktarı
                    // ikincil alanlara yazılır (bkz. Glikol ile aynı desen).
                    softIssue.SecondaryFormValue = gasFormStr;
                    softIssue.SecondaryHakedisValue = hakedisStr;
                    softIssue.SecondaryStatus = gasStatus;
                    results.Add(softIssue);
                }
                else
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                        gasFormStr, hakedisStr, gasStatus, gasExplanation, firstGasItemId));
                }
            }
            else if (softIssue != null)
            {
                results.Add(softIssue);
            }

            // Kaçak bilgisi — henüz sayısal bir kural yok; bilgi amaçlı manuel kontrol notu (iskelet aşaması).
            // Gaz miktarı eşleşmesinden bağımsız, her sayfa için ayrıca kontrol edilir.
            if (!string.IsNullOrWhiteSpace(page.DescriptionRaw) &&
                LeakKeywords.Any(k => TextNormalizationHelper.NormalizeName(page.DescriptionRaw).Contains(k)))
            {
                var snippet = page.DescriptionRaw.Length > 200 ? page.DescriptionRaw[..200] + "…" : page.DescriptionRaw;
                results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Kaçak Bilgisi",
                    null, null, AiComparisonStatus.ManuelKontrol,
                    $"Servis formunda kaçak ile ilgili bilgi bulunuyor: \"{snippet}\" — kaçak yeri/tespiti/onarımı manuel doğrulanmalıdır."));
            }
        }

        // ── Tekrar Ziyaret Uyarısı: sayfa/form eşleşmesinden BAĞIMSIZ, tamamen Excel'deki (hakediş
        // referansı) ziyaret tarihlerine dayanır — aynı mağazaya 4 gün veya daha kısa aralıkla birden
        // fazla gaz müdahalesi yapılmışsa İLK ziyaret hariç her sonraki ziyarete uyarı eklenir (ilk
        // ziyaret yalnızca sonraki karşılaştırmaların referansıdır, kendisi hiçbir zaman işaretlenmez).
        // Her kayıt yalnızca KENDİSİNDEN BİR ÖNCEKİ ziyaretle karşılaştırılır (ilk ziyaretle değil).
        AddRepeatVisitWarnings(job.Id, checkItems, checkItemPageId, results);

        _db.AiComparisonResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static void AddRepeatVisitWarnings(int jobId, List<ProgressPaymentCheckItem> checkItems,
        Dictionary<int, int> checkItemPageId, List<AiComparisonResult> results)
    {
        var visitsByStore = checkItems
            .Where(i => IsGas(i.OriginalMaterialName) && i.VisitDate.HasValue)
            .GroupBy(i => TextNormalizationHelper.StoreKey(i.StoreCode, i.StoreName))
            .Where(g => !string.IsNullOrEmpty(g.Key));

        foreach (var storeGroup in visitsByStore)
        {
            var sorted = storeGroup.OrderBy(i => i.VisitDate!.Value.Date).ToList();
            for (int i = 1; i < sorted.Count; i++)
            {
                var dayGap = (sorted[i].VisitDate!.Value.Date - sorted[i - 1].VisitDate!.Value.Date).Days;
                if (dayGap > RepeatVisitMaxDays) continue;

                var curr = sorted[i];
                var verb = i == 1 ? "tekrar" : "yeniden"; // 2. ziyaret: "tekrar", 3.+ ziyaret: "yeniden"
                var storeLabel = curr.StoreName ?? curr.StoreCode ?? "Bilinmeyen Mağaza";
                checkItemPageId.TryGetValue(curr.Id, out var sourcePageId);
                results.Add(new AiComparisonResult
                {
                    JobId = jobId,
                    StoreLabel = storeLabel,
                    VisitDate = curr.VisitDate,
                    ProgressPaymentCheckItemId = curr.Id,
                    ItemType = AiComparisonItemType.GasUsage,
                    Description = "Tekrar Ziyaret Uyarısı",
                    Status = AiComparisonStatus.ManuelKontrol,
                    Explanation = $"Aynı mağazaya önceki gaz müdahalesinden {dayGap} gün sonra {verb} gaz basılmıştır. Detaylı açıklama lazım.",
                    CreatedAt = DateTime.Now,
                    // Bu ziyaretin eşleştiği servis formu sayfası varsa bağla — "Formu Göster" butonu
                    // bu satırda da görünsün (kullanıcı isteği: "eksik veya hatalı olsa bile yanında
                    // hakedişi formu görebilmem lazım, hepsine form butonunu ekle"). Sayfa yoksa (örn.
                    // bu ziyarete ait form hiç yüklenmemişse) 0 kalır — SourcePageId int? olduğundan
                    // null'a çevrilir.
                    SourcePageId = sourcePageId == 0 ? null : sourcePageId,
                });
            }
        }
    }

    // Kullanıcı talebi: "tüpler 5 kg'dan başlar" — bir soğutucu gaz tüpü/dolumu fiziksel olarak bu
    // değerin altında olamaz. AI'nın okuduğu "1,5 kg" / "2,5 kg" gibi bir değer bu yüzden GERÇEKTE
    // "15 kg" / "25 kg" olup ondalık ayıracı yanlış konumlandırılmış bir OCR hatasıdır — bu düzeltme
    // yalnızca AI'nın kendi okuması için geçerlidir (kullanıcının Düzelt ile ELLE girdiği bir değer asla
    // otomatik çarpılmaz, kullanıcı ne yazdıysa odur).
    private const decimal MinPhysicalGasCylinderKg = 5m;

    /// <summary>Ham okumayı ve (varsa) fiziksel-minimum düzeltmesi uygulanmış son değeri birlikte döner
    /// — çağıran taraf, bir düzeltme yapıldığında bunu açıklamaya yansıtabilsin diye.</summary>
    private static (decimal? Value, decimal? RawBeforeCorrection) NormalizeGasReading(decimal raw)
    {
        if (raw > 0 && raw < MinPhysicalGasCylinderKg) return (raw * 10, raw);
        return (raw, null);
    }

    private static (decimal? Value, decimal? RawBeforeCorrection) ExtractGasKg(AiDocumentPage page)
    {
        // Birden fazla "gaz" eşleşen malzeme olabilir: AI'nın orijinal okuduğu satır VE kullanıcının
        // manuel düzeltme için eklediği sentetik satır (bkz. CorrectSingleItemQuantityAsync). Kullanıcı
        // düzeltmesi HER ZAMAN önceliklidir — aksi halde FirstOrDefault sırayla ilk eşleşeni (genelde
        // AI'nın orijinal, yanlış okuduğu satırı) seçip kullanıcının girdiği miktarı sessizce yok sayardı.
        var gasMaterials = page.Materials.Where(IsGasMaterial).ToList();
        var corrected = gasMaterials.FirstOrDefault(m => m.UserCorrectedQuantity.HasValue);
        if (corrected != null) return (corrected.UserCorrectedQuantity, null); // kullanıcı girdisi — düzeltilmez

        var gasMaterial = gasMaterials.FirstOrDefault();
        if (gasMaterial?.Quantity != null)
            return NormalizeGasReading(gasMaterial.Quantity.Value);

        if (!string.IsNullOrEmpty(page.DescriptionRaw))
        {
            var match = GasKgRegex.Match(page.DescriptionRaw);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return NormalizeGasReading(v);
        }
        return (null, null);
    }
}

/// <summary>
/// GLİKOL KULLANIM HAKEDİŞİ için özel karşılaştırma: hakedişteki glikol kg'ı servis formundaki
/// "KULLANILAN MALZEME" tablosundan çıkarılan glikol kg'ı ile karşılaştırılır. KRİTİK KURAL: bu
/// kategoride YALNIZCA glikol kontrol edilir — formda bulunup Excel'de talep edilmemiş diğer
/// malzemeler (gaz, filtre, dryer, vana, boru, flex, sensör, yağ vb.) hiçbir şekilde değerlendirilmez,
/// hiçbir uyarı üretilmez (Excel referanstır — bkz. DefaultCategoryComparisonStrategy üstündeki
/// açıklama). Eşleştirme FormNumberMatcher ile form no → mağaza → tarih sırasıyla yapılır.
/// </summary>
public class GlycolUsageComparisonStrategy : ICategoryComparisonStrategy
{
    private const decimal GlycolQuantityTolerance = 0.01m;
    private static readonly Regex GlycolKgRegex = new(@"(\d+(?:[.,]\d+)?)\s*kg", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] GlycolKeywords = { "glikol", "antifriz" }; // TextNormalizationHelper Türkçe karakterleri normalize eder

    private readonly AppDbContext _db;
    public GlycolUsageComparisonStrategy(AppDbContext db) => _db = db;

    public HakedisCategory? Category => HakedisCategory.GlycolUsage;
    public string? SingleItemLabel => "Glikol Miktarı (kg)";

    public async Task BuildAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AiComparisonResults.Where(r => r.JobId == job.Id).ToListAsync(cancellationToken);
        _db.AiComparisonResults.RemoveRange(existing);

        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == job.Id && p.DocumentType == AiDocumentType.ServiceForm)
            .Include(p => p.Materials)
            .ToListAsync(cancellationToken);

        var checkItems = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        var results = new List<AiComparisonResult>();

        foreach (var page in pages)
        {
            // MatchWithSoftIssue: mağaza/tarih uyuşmazlığı (softIssue) olsa bile eşleşen grup (sameVisit)
            // döner — böylece glikol miktarı BAĞIMSIZ olarak hesaplanıp AYNI SATIRA eklenebilir. Yalnızca
            // gerçekten eşleşen bir kayıt yoksa (hardError — form no okunamadı/Excel'de yok/mükerrer)
            // hiçbir hesaplama yapılamaz.
            var (sameVisit, hardError, softIssue) = FormNumberMatcher.MatchWithSoftIssue(job.Id, page, checkItems);
            if (hardError != null) { results.Add(hardError); continue; }

            var first = sameVisit![0];
            var storeLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";

            // Excel referanstır: hakedişte glikol TALEP EDİLMEMİŞSE, formda glikoldan bahsedilmesi
            // tek başına bir talep oluşturmaz — hiçbir sonuç üretilmez (kural 6: yalnızca glikol).
            // Soft issue varsa (mağaza/tarih) yine de kendi başına raporlanır.
            var hakedisGlycolItems = sameVisit.Where(i => IsGlycol(i.OriginalMaterialName)).ToList();
            if (hakedisGlycolItems.Count == 0)
            {
                if (softIssue != null) results.Add(softIssue);
                continue;
            }

            var formGlycolKg = ExtractGlycolKg(page);
            var hakedisGlycolKg = hakedisGlycolItems.Sum(i => i.Quantity);
            var firstGlycolItemId = hakedisGlycolItems[0].Id;

            AiComparisonStatus glycolStatus;
            string glycolFormStr, glycolExplanation;
            var hakedisStr = $"{hakedisGlycolKg:0.##} kg";

            if (!formGlycolKg.HasValue)
            {
                // GasUsage ile aynı desen (bkz. ExtractGasKg çağrısı) — form var ama miktar okunamadı,
                // bu "Eksik" değil "Manuel Kontrol"dür: kullanıcı formu bizzat okuyup Düzelt ile miktarı
                // girebilir (bkz. CorrectSingleItemQuantityAsync). Eskiden Eksik kullanılıyordu ama bu
                // kategori için Eksik filtresi/rozeti hiçbir zaman anlamlı dolmuyordu.
                glycolStatus = AiComparisonStatus.ManuelKontrol;
                glycolFormStr = "Okunamadı";
                glycolExplanation = $"Hakedişte glikol kalemi var ancak servis formunda glikol kg bilgisi açıkça bulunamadı — manuel kontrol edilmeli.";
            }
            else
            {
                glycolFormStr = $"{formGlycolKg.Value:0.##} kg";
                if (Math.Abs(formGlycolKg.Value - hakedisGlycolKg) <= GlycolQuantityTolerance)
                {
                    glycolStatus = AiComparisonStatus.Uygun;
                    glycolExplanation = $"\"{page.FormNumber}\" numaralı servis formunda {glycolFormStr} glikol kullanımı doğrulanmış, hakedişteki miktarla uyumludur.";
                }
                else if (QuantityOcrHelper.IsDecimalShiftMatch(formGlycolKg.Value, hakedisGlycolKg, GlycolQuantityTolerance))
                {
                    // OCR'ın ondalık ayırıcıyı yanlış eklediği/bir basamak atladığı çok yaygın bir hata
                    // (20→2, 15→1.5) — gerçek okuma tooltip'te görünür kalır, yalnızca Durum düzeltilir.
                    glycolStatus = AiComparisonStatus.Uygun;
                    glycolExplanation = $"\"{page.FormNumber}\" numaralı servis formunda {glycolFormStr} okunmuştur — muhtemel ondalık basamak okuma hatası nedeniyle hakedişteki {hakedisStr} ile eşleştirilmiş ve uygun kabul edilmiştir.";
                }
                else
                {
                    glycolStatus = AiComparisonStatus.UygunDegil;
                    glycolExplanation = $"\"{page.FormNumber}\" numaralı servis formunda {glycolFormStr} glikol kullanımı doğrulanırken hakedişte {hakedisStr} talep edilmiştir.";
                }
            }

            if (softIssue != null)
            {
                // Satırın ANA konusu (Description/Status/Durum) mağaza/tarih sorunudur — ama glikol
                // miktarını da aynı satırda göstermek için ikincil alanlara yazılır (bkz. AŞAMA 1: Glikol
                // Miktarı bağımsız bir kolon). Manuel onay yalnızca ana Status'ü değiştirir, ikincil
                // alanlara dokunmaz (bkz. AiAnalysisPipelineService.ApplyOverridesAsync).
                softIssue.SecondaryFormValue = glycolFormStr;
                softIssue.SecondaryHakedisValue = hakedisStr;
                softIssue.SecondaryStatus = glycolStatus;
                results.Add(softIssue);
            }
            else
            {
                results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GlycolUsage, "Glikol Miktarı (kg)",
                    glycolFormStr, hakedisStr, glycolStatus, glycolExplanation, firstGlycolItemId));
            }
        }

        _db.AiComparisonResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsGlycol(string? materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return false;
        var norm = TextNormalizationHelper.NormalizeName(materialName);
        return GlycolKeywords.Any(k => norm.Contains(k));
    }

    private static decimal? ExtractGlycolKg(AiDocumentPage page)
    {
        // ExtractGasKg ile aynı öncelik kuralı — bkz. oradaki açıklama: kullanıcı düzeltmesi varsa
        // her zaman o kazanır, AI'nın orijinal (yanlış) okuması sessizce üzerine yazamaz.
        var glycolMaterials = page.Materials.Where(m => IsGlycol(m.RawName) || IsGlycol(m.NormalizedName)).ToList();
        var correctedGlycol = glycolMaterials.FirstOrDefault(m => m.UserCorrectedQuantity.HasValue);
        if (correctedGlycol != null) return correctedGlycol.UserCorrectedQuantity;

        var glycolMaterial = glycolMaterials.FirstOrDefault();
        if (glycolMaterial != null)
            return glycolMaterial.Quantity;

        if (IsGlycol(page.DescriptionRaw))
        {
            var match = GlycolKgRegex.Match(page.DescriptionRaw!);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return v;
        }
        return null;
    }
}

/// <summary>
/// İLAVE İŞLER hakedişi için özel karşılaştırma: malzeme ve adam-saat kontrolü
/// <see cref="DefaultCategoryComparisonStrategy"/> ile aynıdır (Excel referanstır — bkz. o sınıfın
/// üstündeki açıklama), ancak servis ücreti kuralları farklıdır: aynı ziyarette (aynı form no + aynı
/// mağaza/tarih) birden fazla Şehiriçi/Şehirdışı Servis Ücreti MÜKERRERDİR. Hakedişte hiç servis ücreti
/// talep edilmemişse (Excel referanstır) hiçbir sonuç üretilmez. Farklı tarihli gerçek ayrı ziyaretlerin
/// servis ücretleri birbirini etkilemez (her biri kendi FormNumberMatcher grubunda ayrı değerlendirilir).
/// Eşleştirme FormNumberMatcher ile form numarası üzerinden yapılır.
/// </summary>
public class AdditionalWorkComparisonStrategy : ICategoryComparisonStrategy
{
    private const decimal ManHoursTolerance = 0.1m;
    private const decimal MaterialQuantityTolerance = 0.01m;

    // Gerçek hakediş dosyasında ("İÇ ANADOLU_İNTİKOŞ MİGROS SOĞUTMA İLAVE İŞLER") bu iki iş kalemi hep
    // sabit MALZEME KODU (S1-S4) ile geliyor — kod bazlı eşleştirme metin eşleştirmesinden çok daha
    // güvenilir (bkz. aşağıdaki metin yedeği: eskiden TextNormalizationHelper.NormalizeName sonucu
    // KELİMELER ARASINDA BOŞLUK BIRAKTIĞI için ör. "sehirici" hiçbir zaman eşleşmiyordu — "1 EKIP ŞEHİR
    // İÇİ SERVİS BEDELİ" normalize edilince "1 ekip sehir ici servis bedeli" olur, boşluksuz "sehirici"
    // bunun içinde hiç geçmez. Bu satır hem adam-saat hem şehir içi/dışı tespitini etkileyen, üretimde
    // muhtemelen hiç çalışmamış bir hataydı — kod bazlı eşleştirmeyle birlikte düzeltildi).
    private const string SehirIciCode = "S1";
    private const string SehirDisiCode = "S2";
    private static readonly string[] ManHoursCodes = { "S3", "S4" };

    private readonly AppDbContext _db;
    public AdditionalWorkComparisonStrategy(AppDbContext db) => _db = db;

    private static bool IsManHoursItem(ProgressPaymentCheckItem i) =>
        i.IsServiceItem && (
            ManHoursCodes.Any(c => string.Equals(i.OriginalItemCode?.Trim(), c, StringComparison.OrdinalIgnoreCase)) ||
            TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("adam saat"));

    private enum ServiceFeeType { Unknown, SehirIci, SehirDisi }

    private static ServiceFeeType GetServiceFeeType(ProgressPaymentCheckItem i)
    {
        var code = i.OriginalItemCode?.Trim();
        if (string.Equals(code, SehirIciCode, StringComparison.OrdinalIgnoreCase)) return ServiceFeeType.SehirIci;
        if (string.Equals(code, SehirDisiCode, StringComparison.OrdinalIgnoreCase)) return ServiceFeeType.SehirDisi;
        var norm = TextNormalizationHelper.NormalizeName(i.OriginalMaterialName);
        if (norm.Contains("sehir ici")) return ServiceFeeType.SehirIci;
        if (norm.Contains("sehir disi")) return ServiceFeeType.SehirDisi;
        return ServiceFeeType.Unknown;
    }

    private static bool IsServiceFeeItem(ProgressPaymentCheckItem i) =>
        i.IsServiceItem && GetServiceFeeType(i) != ServiceFeeType.Unknown;

    private static string FeeTypeLabel(ServiceFeeType t) => t switch
    {
        ServiceFeeType.SehirIci => "şehir içi",
        ServiceFeeType.SehirDisi => "şehir dışı",
        _ => "bilinmeyen",
    };

    public HakedisCategory? Category => HakedisCategory.AdditionalWork;
    public string? SingleItemLabel => null; // çok kalemli (iş kalemi listesi) kategori — tekil etiket yok

    public async Task BuildAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await _db.AiComparisonResults.Where(r => r.JobId == job.Id).ToListAsync(cancellationToken);
        _db.AiComparisonResults.RemoveRange(existing);

        var pages = await _db.AiDocumentPages
            .Where(p => p.JobId == job.Id && p.DocumentType == AiDocumentType.ServiceForm)
            .Include(p => p.Materials)
            .ToListAsync(cancellationToken);

        var checkItems = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        // Kullanıcının daha önce onayladığı (Uygun'a çevirdiği) sonuçlar — mağaza/tarih uyuşmazlığı
        // onaylanmışsa FormNumberMatcher bunu eşleşti sayıp kategori kontrolünün çalışmasına izin verir.
        var overriddenKeys = (await _db.AiComparisonOverrides
            .Where(o => o.JobId == job.Id)
            .Select(o => o.MatchKey)
            .ToListAsync(cancellationToken)).ToHashSet();

        var results = new List<AiComparisonResult>();

        foreach (var page in pages)
        {
            var (sameVisit, error) = FormNumberMatcher.Match(job.Id, page, checkItems, overriddenKeys);
            if (error != null) { results.Add(error); continue; }

            var first = sameVisit![0];
            var storeLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";

            // ── Malzeme: hakediş → form (EXCEL REFERANSTIR — bkz. DefaultCategoryComparisonStrategy
            // üstündeki aynı bloğun açıklaması. Formda olup Excel'de talep edilmemiş malzemeler
            // kontrol dışıdır, hiçbir sonuç üretilmez.) ──
            foreach (var item in sameVisit.Where(i => !i.IsServiceItem))
            {
                var searchName = TextNormalizationHelper.NormalizeName(item.OriginalMaterialName);
                var candidate = page.Materials
                    .Select(m => (Mat: m, Score: TextNormalizationHelper.SimilarityRatio(searchName, TextNormalizationHelper.NormalizeName(m.NormalizedName ?? m.RawName))))
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                var hakedisStr = $"{item.Quantity:0.##} {item.Unit}";

                if (candidate.Mat != null && candidate.Score >= 0.6)
                {
                    var effectiveQty = candidate.Mat.UserCorrectedQuantity ?? candidate.Mat.Quantity;
                    var formStr = effectiveQty.HasValue ? $"{effectiveQty.Value:0.##} {candidate.Mat.UserCorrectedUnit ?? candidate.Mat.Unit}" : "Okunamadı";

                    if (!effectiveQty.HasValue || candidate.Mat.RequiresManualReview)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.ManuelKontrol,
                            $"\"{item.OriginalMaterialName}\" için servis formunda okunan miktar belirsiz — manuel kontrol edilmeli.", item.Id, candidate.Mat.Id));
                    }
                    else if (Math.Abs(effectiveQty.Value - item.Quantity) <= MaterialQuantityTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Hakedişte talep edilen miktar servis formunda doğrulanmıştır.", item.Id, candidate.Mat.Id));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Hakedişte {hakedisStr} talep edilmiş, servis formunda {formStr} doğrulanmıştır.", item.Id, candidate.Mat.Id));
                    }
                }
                else
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                        "—", hakedisStr, AiComparisonStatus.Eksik,
                        $"Hakedişte talep edilen \"{item.OriginalMaterialName}\" servis formunda doğrulanamadı.", item.Id));
                }
            }

            // ── Adam-saat — yalnızca hakedişte adam-saat kalemi TALEP EDİLMİŞSE kontrol edilir ──
            var hakedisManHoursItems = sameVisit.Where(IsManHoursItem).ToList();
            if (hakedisManHoursItems.Count > 0)
            {
                var hakedisManHours = hakedisManHoursItems.Sum(i => i.Quantity);
                if (page.PayableManHours.HasValue)
                {
                    var formStr = $"{page.PayableManHours.Value:0.##} saat";
                    var hakedisStr = $"{hakedisManHours:0.##} saat";
                    if (Math.Abs(page.PayableManHours.Value - hakedisManHours) <= ManHoursTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Formdaki çalışma sürelerine göre hesaplanan adam-saat hakedişle uyumlu."));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Formdaki çalışma sürelerine göre toplam {page.CalculatedManHours:0.##} adam-saat oluşmaktadır. " +
                            $"Kural gereği 4 saat düşülerek en fazla {page.PayableManHours:0.##} adam-saat ödenebilir."));
                    }
                }
                else
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ManHours, "Adam-Saat",
                        "Okunamadı", null, AiComparisonStatus.ManuelKontrol,
                        "Hakedişte adam-saat kalemi var ancak servis formunda personel/çalışma saati bilgisi bulunamadı — tahmin edilmedi."));
                }
            }

            // ── Servis ücreti (şehir içi/şehir dışı) — İLAVE İŞLER'e özel kural: yalnızca hakedişte
            // TALEP EDİLMİŞSE kontrol edilir (Excel referanstır); aynı ziyarette (aynı form no +
            // aynı mağaza/tarih grubu — bkz. FormNumberMatcher adım 2) birden fazlası MÜKERRERDİR.
            var serviceFeeItems = sameVisit.Where(IsServiceFeeItem).ToList();

            if (serviceFeeItems.Count > 1)
            {
                foreach (var fee in serviceFeeItems)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, "Mükerrer Servis Ücreti",
                        fee.OriginalMaterialName, $"{fee.Quantity:0.##} adet", AiComparisonStatus.UygunDegil,
                        "Aynı mağaza ve aynı servis tarihi için birden fazla servis ücreti talep edilmiştir.", fee.Id));
                }
            }
            else if (serviceFeeItems.Count == 1)
            {
                var fee = serviceFeeItems[0];
                if (page.ServiceFeeRejectedDueToMaintenance)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                        "Periyodik bakım mevcut", $"{fee.Quantity:0.##} adet", AiComparisonStatus.UygunDegil,
                        $"{page.ServiceDate:dd.MM.yyyy} tarihinde bu mağazada periyodik bakım bulunmaktadır. " +
                        "Aynı tarih için ayrıca şehir içi/şehir dışı servis ücreti ödenemez.", fee.Id));
                }
                else
                {
                    // Kullanıcı talebi: "Ankara dışındaki illere gidiyorsa EXCELDEN kontrol edilip
                    // '1 EKİP ŞEHİR DIŞI SERVİS BEDELİ' verilmelidir. Ankara içi ise şehir içidir. Bunu
                    // formdan bulmana gerek yok." — mağazanın hangi ilde olduğu Excel'deki "Mağazalar"
                    // master sayfasından (bkz. ProgressPaymentExcelParser.BuildStoreCityMap) gelir;
                    // servis formu bu kararda HİÇ kullanılmaz. Aynı ziyaretteki kalemlerden biri
                    // StoreCity taşıyorsa (hepsi aynı mağazaya ait olduğundan hangisi fark etmez) yeterlidir.
                    var storeCity = sameVisit.Select(i => i.StoreCity).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                    var feeType = GetServiceFeeType(fee);

                    if (string.IsNullOrWhiteSpace(storeCity))
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                            $"{FeeTypeLabel(feeType)} talep edilmiş", $"{fee.Quantity:0.##} adet", AiComparisonStatus.ManuelKontrol,
                            "Bu mağazanın hangi ilde olduğu hakediş Excel'indeki \"Mağazalar\" listesinden bulunamadı — " +
                            "şehir içi/şehir dışı servis bedeli türü doğrulanamadı, manuel kontrol edilmeli.", fee.Id));
                    }
                    else
                    {
                        var isAnkara = TextNormalizationHelper.NormalizeName(storeCity) == "ankara";
                        var expectedType = isAnkara ? ServiceFeeType.SehirIci : ServiceFeeType.SehirDisi;

                        if (feeType == expectedType)
                        {
                            results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                                "Servis ziyareti mevcut", $"{fee.Quantity:0.##} adet", AiComparisonStatus.Uygun,
                                $"Mağaza {storeCity} ilinde — {FeeTypeLabel(expectedType)} servis bedeli doğru talep edilmiştir.", fee.Id));
                        }
                        else
                        {
                            results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                                FeeTypeLabel(feeType), FeeTypeLabel(expectedType), AiComparisonStatus.UygunDegil,
                                $"Mağaza {storeCity} ilinde — bu yüzden {FeeTypeLabel(expectedType)} servis bedeli talep edilmesi gerekirken " +
                                $"hakedişte {FeeTypeLabel(feeType)} servis bedeli talep edilmiştir.", fee.Id));
                        }
                    }
                }
            }
            // serviceFeeItems.Count == 0: hakedişte servis ücreti talep edilmemiş — Excel referanstır,
            // formda ziyaret olması tek başına bir talep oluşturmadığı için hiçbir sonuç üretilmez.
        }

        _db.AiComparisonResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Kategoriye göre karşılaştırma stratejisini seçer; eşleşme yoksa genel/varsayılan stratejiye düşer.</summary>
public class CategoryComparisonStrategyRegistry : ICategoryComparisonStrategyRegistry
{
    private readonly Dictionary<HakedisCategory, ICategoryComparisonStrategy> _byCategory;
    private readonly ICategoryComparisonStrategy _default;

    public CategoryComparisonStrategyRegistry(IEnumerable<ICategoryComparisonStrategy> strategies)
    {
        var all = strategies.ToList();
        _default = all.First(s => s.Category is null);
        _byCategory = all.Where(s => s.Category.HasValue).ToDictionary(s => s.Category!.Value);
    }

    public ICategoryComparisonStrategy Get(HakedisCategory? category) =>
        category.HasValue && _byCategory.TryGetValue(category.Value, out var strategy) ? strategy : _default;
}
