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
        AiComparisonStatus status, string explanation, int? checkItemId = null) => new()
    {
        JobId = jobId,
        StoreId = page.MatchedStoreId,
        StoreLabel = storeLabel,
        VisitDate = page.ServiceDate,
        SourcePageId = page.Id,
        ProgressPaymentCheckItemId = checkItemId,
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
    /// <paramref name="overriddenMatchKeys"/>: kullanıcının "Onay ver" ile Uygun'a çevirdiği sonuçların
    /// kalıcı anahtarları (bkz. ComparisonResultFactory.ComputeMatchKey). Adım 3/4'te (mağaza/tarih)
    /// üretilecek hata bu sette varsa, hata döndürmek yerine eşleşti kabul edilir (group, null) — kullanıcı
    /// zaten "bu doğru mağaza/tarih" demiştir, kategori kontrolünün gerçek bir sonuç üretmesi sağlanır.
    /// Adım 1/2'de (form no okunamadı/Excel'de yok/mükerrer) kurtarma yoktur — eşleşecek bir aday satır yok.
    /// </summary>
    public static (List<ProgressPaymentCheckItem>? Matched, AiComparisonResult? Error) Match(
        int jobId, AiDocumentPage page, List<ProgressPaymentCheckItem> checkItems, HashSet<string> overriddenMatchKeys)
    {
        var label = StoreLabelFallback(page);

        // 1) Form numarası okunabildi mi? Düşük güvenle rastgele eşleştirme YAPILMAZ.
        var formNo = TextNormalizationHelper.NormalizeCode(page.FormNumber ?? string.Empty);
        var lowConfidence = page.FormNumberConfidence.HasValue && page.FormNumberConfidence.Value < MinFormNumberConfidence;
        if (string.IsNullOrEmpty(formNo) || lowConfidence)
        {
            return (null, ComparisonResultFactory.New(jobId, page, label, AiComparisonItemType.Material,
                "Form Numarası Okunamadı", page.FormNumber, null, AiComparisonStatus.ManuelKontrol,
                "Servis formu numarası okunamadığı için hakediş Excelindeki ilgili kayıt güvenilir şekilde tespit edilemedi."));
        }

        // 2) Form numarasını hakediş Excelinde ara (MaintenanceFormNo — import sırasında Excel'den okunmuştu).
        var candidates = checkItems
            .Where(i => !string.IsNullOrWhiteSpace(i.MaintenanceFormNo)
                        && TextNormalizationHelper.NormalizeCode(i.MaintenanceFormNo) == formNo)
            .ToList();

        if (candidates.Count == 0)
        {
            return (null, ComparisonResultFactory.New(jobId, page, label, AiComparisonItemType.Material,
                "Form Hakedişte Bulunamadı", page.FormNumber, "—", AiComparisonStatus.Eksik,
                $"\"{page.FormNumber}\" numaralı servis formunun yüklenen hakediş Excelinde karşılığı bulunamadı."));
        }

        // Aynı form numarasına ait satırlar farklı mağaza/tarihe dağılıyorsa mükerrer numaralandırma var demektir.
        var visitGroups = candidates
            .GroupBy(i => (Store: TextNormalizationHelper.NormalizeCode(i.StoreCode ?? i.StoreName ?? string.Empty), Date: i.VisitDate?.Date))
            .ToList();
        if (visitGroups.Count > 1)
        {
            return (null, ComparisonResultFactory.New(jobId, page, label, AiComparisonItemType.Material,
                "Mükerrer Form Numarası", page.FormNumber, null, AiComparisonStatus.ManuelKontrol,
                $"\"{page.FormNumber}\" numaralı form hakedişte birden fazla farklı mağaza/tarihe ait kayıtla eşleşiyor — manuel kontrol edilmelidir."));
        }

        var group = visitGroups[0].ToList();
        var first = group[0];
        var hakedisStoreLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";

        // Kullanıcı bu formun mağaza/tarih uyuşmazlığını "Onay ver" ile zaten düzeltmişse (bkz.
        // AiComparisonOverride), hata döndürmek yerine eşleşti kabul edip kategori kontrolünün normal
        // şekilde çalışmasına (gerçek bir sonuç üretmesine) izin verilir.
        (List<ProgressPaymentCheckItem>? Matched, AiComparisonResult? Error) RecoverableError(AiComparisonResult candidateError) =>
            overriddenMatchKeys.Contains(ComparisonResultFactory.ComputeMatchKey(candidateError)) ? (group, null) : (null, candidateError);

        // 3) Mağaza doğrulama — kod varsa öncelikli/kesin kriter, yoksa isim benzerliğine (sınırlı) düşülür.
        var storeCheck = CompareStore(page, first);
        if (storeCheck == StoreCheck.Mismatch)
        {
            return RecoverableError(ComparisonResultFactory.New(jobId, page, hakedisStoreLabel, AiComparisonItemType.Material,
                "Mağaza Uyuşmazlığı", page.StoreCodeRaw ?? page.StoreNameRaw, hakedisStoreLabel, AiComparisonStatus.UygunDegil,
                $"\"{page.FormNumber}\" numaralı form üzerindeki mağaza {(page.StoreCodeRaw ?? page.StoreNameRaw)} olarak okunmuştur " +
                $"ancak hakediş Excelindeki aynı form numarası farklı mağazaya ({hakedisStoreLabel}) aittir.", first.Id));
        }
        if (storeCheck == StoreCheck.Inconclusive)
        {
            return RecoverableError(ComparisonResultFactory.New(jobId, page, hakedisStoreLabel, AiComparisonItemType.Material,
                "Mağaza Doğrulanamadı", page.StoreCodeRaw ?? page.StoreNameRaw, hakedisStoreLabel, AiComparisonStatus.ManuelKontrol,
                $"\"{page.FormNumber}\" numaralı formdaki mağaza bilgisi hakediş kaydıyla yeterli güvenle karşılaştırılamadı — manuel kontrol edilmelidir.", first.Id));
        }

        // 4) Tarih doğrulama — ikisi de doluyken farklıysa hata; biri boşsa (yetersiz veri) engelleme.
        if (page.ServiceDate.HasValue && first.VisitDate.HasValue && page.ServiceDate.Value.Date != first.VisitDate.Value.Date)
        {
            return RecoverableError(ComparisonResultFactory.New(jobId, page, hakedisStoreLabel, AiComparisonItemType.Material,
                "Tarih Uyuşmazlığı", page.ServiceDate.Value.ToString("dd.MM.yyyy"), first.VisitDate.Value.ToString("dd.MM.yyyy"),
                AiComparisonStatus.UygunDegil,
                $"Servis formu tarihi {page.ServiceDate:dd.MM.yyyy}, hakediş Excelindeki tarih {first.VisitDate:dd.MM.yyyy} olarak görülmektedir.", first.Id));
        }

        return (group, null);
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
                            $"\"{item.OriginalMaterialName}\" için servis formunda okunan miktar belirsiz — manuel kontrol edilmeli.", item.Id));
                    }
                    else if (Math.Abs(effectiveQty.Value - item.Quantity) <= MaterialQuantityTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Hakedişte talep edilen miktar servis formunda doğrulanmıştır.", item.Id));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Hakedişte {hakedisStr} talep edilmiş, servis formunda {formStr} doğrulanmıştır.", item.Id));
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
    private static readonly Regex GasKgRegex = new(@"(\d+(?:[.,]\d+)?)\s*kg", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] LeakKeywords = { "kacak" }; // TextNormalizationHelper "kaçak"→"kacak" çevirir

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

            // Excel referanstır: hakedişte gaz kalemi TALEP EDİLMEMİŞSE, formda gaz kullanımından
            // bahsedilmesi tek başına bir talep oluşturmaz — hiçbir sonuç üretilmez.
            var hakedisGasItems = sameVisit.Where(i => TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("gaz")).ToList();
            if (hakedisGasItems.Count > 0)
            {
                var formGasKg = ExtractGasKg(page);
                var hakedisGasKg = hakedisGasItems.Sum(i => i.Quantity);
                var firstGasItemId = hakedisGasItems[0].Id;

                if (!formGasKg.HasValue)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                        "Okunamadı", $"{hakedisGasKg:0.##} kg", AiComparisonStatus.ManuelKontrol,
                        "Hakedişte gaz kalemi var ancak servis formunda gaz kg bilgisi açıkça bulunamadı — manuel kontrol edilmeli.", firstGasItemId));
                }
                else
                {
                    var formStr = $"{formGasKg.Value:0.##} kg";
                    var hakedisStr = $"{hakedisGasKg:0.##} kg";
                    if (Math.Abs(formGasKg.Value - hakedisGasKg) <= GasQuantityTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Hakedişteki gaz miktarı servis formuyla uyumlu.", firstGasItemId));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Gaz Miktarı (kg)",
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Hakedişte {hakedisStr} gaz belirtilmiş, servis formunda {formStr} tespit edilmiştir.", firstGasItemId));
                    }
                }
            }

            // Kaçak bilgisi — henüz sayısal bir kural yok; bilgi amaçlı manuel kontrol notu (iskelet aşaması).
            if (!string.IsNullOrWhiteSpace(page.DescriptionRaw) &&
                LeakKeywords.Any(k => TextNormalizationHelper.NormalizeName(page.DescriptionRaw).Contains(k)))
            {
                var snippet = page.DescriptionRaw.Length > 200 ? page.DescriptionRaw[..200] + "…" : page.DescriptionRaw;
                results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GasUsage, "Kaçak Bilgisi",
                    null, null, AiComparisonStatus.ManuelKontrol,
                    $"Servis formunda kaçak ile ilgili bilgi bulunuyor: \"{snippet}\" — kaçak yeri/tespiti/onarımı manuel doğrulanmalıdır."));
            }
        }

        _db.AiComparisonResults.AddRange(results);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static decimal? ExtractGasKg(AiDocumentPage page)
    {
        var gasMaterial = page.Materials.FirstOrDefault(m =>
            TextNormalizationHelper.NormalizeName(m.RawName ?? m.NormalizedName ?? string.Empty).Contains("gaz"));
        if (gasMaterial != null)
            return gasMaterial.UserCorrectedQuantity ?? gasMaterial.Quantity;

        if (!string.IsNullOrEmpty(page.DescriptionRaw))
        {
            var match = GasKgRegex.Match(page.DescriptionRaw);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return v;
        }
        return null;
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

            // Excel referanstır: hakedişte glikol TALEP EDİLMEMİŞSE, formda glikoldan bahsedilmesi
            // tek başına bir talep oluşturmaz — hiçbir sonuç üretilmez (kural 6: yalnızca glikol).
            var hakedisGlycolItems = sameVisit.Where(i => IsGlycol(i.OriginalMaterialName)).ToList();
            if (hakedisGlycolItems.Count == 0) continue;

            var formGlycolKg = ExtractGlycolKg(page);
            var hakedisGlycolKg = hakedisGlycolItems.Sum(i => i.Quantity);
            var firstGlycolItemId = hakedisGlycolItems[0].Id;

            if (!formGlycolKg.HasValue)
            {
                results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GlycolUsage, "Glikol Miktarı (kg)",
                    "Okunamadı", $"{hakedisGlycolKg:0.##} kg", AiComparisonStatus.Eksik,
                    $"\"{page.FormNumber}\" numaralı servis formunda glikol kullanımı doğrulanamadı (Glikol Formda Doğrulanamadı) " +
                    $"— hakedişte {hakedisGlycolKg:0.##} kg glikol talep edilmiştir.", firstGlycolItemId));
            }
            else
            {
                var formStr = $"{formGlycolKg.Value:0.##} kg";
                var hakedisStr = $"{hakedisGlycolKg:0.##} kg";
                if (Math.Abs(formGlycolKg.Value - hakedisGlycolKg) <= GlycolQuantityTolerance)
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GlycolUsage, "Glikol Miktarı (kg)",
                        formStr, hakedisStr, AiComparisonStatus.Uygun,
                        $"\"{page.FormNumber}\" numaralı servis formunda {formStr} glikol kullanımı doğrulanmış, hakedişteki miktarla uyumludur.", firstGlycolItemId));
                }
                else
                {
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.GlycolUsage, "Glikol Miktarı (kg)",
                        formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                        $"\"{page.FormNumber}\" numaralı servis formunda {formStr} glikol kullanımı doğrulanırken hakedişte {hakedisStr} talep edilmiştir.", firstGlycolItemId));
                }
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
        var glycolMaterial = page.Materials.FirstOrDefault(m => IsGlycol(m.RawName) || IsGlycol(m.NormalizedName));
        if (glycolMaterial != null)
            return glycolMaterial.UserCorrectedQuantity ?? glycolMaterial.Quantity;

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

    private readonly AppDbContext _db;
    public AdditionalWorkComparisonStrategy(AppDbContext db) => _db = db;

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
                            $"\"{item.OriginalMaterialName}\" için servis formunda okunan miktar belirsiz — manuel kontrol edilmeli.", item.Id));
                    }
                    else if (Math.Abs(effectiveQty.Value - item.Quantity) <= MaterialQuantityTolerance)
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.Uygun, "Hakedişte talep edilen miktar servis formunda doğrulanmıştır.", item.Id));
                    }
                    else
                    {
                        results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.Material, item.OriginalMaterialName,
                            formStr, hakedisStr, AiComparisonStatus.UygunDegil,
                            $"Hakedişte {hakedisStr} talep edilmiş, servis formunda {formStr} doğrulanmıştır.", item.Id));
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

            // ── Servis ücreti (şehiriçi/şehirdışı) — İLAVE İŞLER'e özel kural: yalnızca hakedişte
            // TALEP EDİLMİŞSE kontrol edilir (Excel referanstır); aynı ziyarette (aynı form no +
            // aynı mağaza/tarih grubu — bkz. FormNumberMatcher adım 2) birden fazlası MÜKERRERDİR.
            var serviceFeeItems = sameVisit.Where(i => i.IsServiceItem &&
                (TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("sehirici") ||
                 TextNormalizationHelper.NormalizeName(i.OriginalMaterialName).Contains("sehirdisi"))).ToList();

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
                    results.Add(ComparisonResultFactory.New(job.Id, page, storeLabel, AiComparisonItemType.ServiceFee, fee.OriginalMaterialName,
                        "Servis ziyareti mevcut", $"{fee.Quantity:0.##} adet", AiComparisonStatus.Uygun,
                        "Servis ücreti mevcut ve uygundur.", fee.Id));
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
