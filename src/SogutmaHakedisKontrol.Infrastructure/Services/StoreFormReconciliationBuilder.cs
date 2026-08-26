using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Kategoriden bağımsız, tüm hakediş türlerinde geçerli mağaza/form eşleşme özeti. Ayrı bir mağaza
/// listesi kullanılmaz — hakediş Excelindeki mağazalarla, servis formlarından FormNumberMatcher
/// tarafından zaten üretilen eşleşme/hata sonuçları mağaza bazında bir araya getirilir:
/// Exceldeki mağazalardan hangilerinin formu var, formlardaki hangi mağazaların Excel karşılığı yok.
/// </summary>
internal static class StoreFormReconciliationBuilder
{
    public sealed class StoreReconciliationResult
    {
        public int ExceldekiMagazaCount { get; init; }
        public int FormlardaBulunanMagazaCount { get; init; }
        public int TamEslesenMagazaCount { get; init; }
        public int EksikMagazaCount { get; init; }
        public int FazlaYabanciMagazaCount { get; init; }
        public int EslesmeyenFormCount { get; init; }

        public List<(string StoreLabel, List<ProgressPaymentCheckItem> Items)> EksikMagazalar { get; init; } = new();
        public List<(string StoreLabel, AiDocumentPage Page)> FazlaYabanciMagazalar { get; init; } = new();
        public List<(string StoreLabel, AiComparisonResult Error)> EslesmeyenFormlar { get; init; } = new();
    }

    public static StoreReconciliationResult Compute(int jobId, List<AiDocumentPage> serviceFormPages, List<ProgressPaymentCheckItem> checkItems)
    {
        var excelStoreKeys = checkItems
            .Select(i => (Key: TextNormalizationHelper.StoreKey(i.StoreCode, i.StoreName), Item: i))
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .ToList();
        var excelStoreKeySet = excelStoreKeys.Select(x => x.Key).ToHashSet();

        var matchedStoreKeys = new HashSet<string>();
        var formStoreKeys = new Dictionary<string, (string Label, AiDocumentPage Page)>();
        var eslesmeyenFormlar = new List<(string StoreLabel, AiComparisonResult Error)>();

        foreach (var page in serviceFormPages)
        {
            var rawKey = TextNormalizationHelper.StoreKey(page.StoreCodeRaw, page.StoreNameRaw);
            if (!string.IsNullOrEmpty(rawKey) && !formStoreKeys.ContainsKey(rawKey))
                formStoreKeys[rawKey] = (page.StoreNameRaw ?? page.StoreCodeRaw ?? "Bilinmeyen Mağaza", page);

            var (matched, error) = FormNumberMatcher.Match(jobId, page, checkItems);
            if (error != null)
            {
                eslesmeyenFormlar.Add((page.StoreNameRaw ?? page.StoreCodeRaw ?? "Bilinmeyen Mağaza", error));

                // Form numarası Excel'de bulundu (ProgressPaymentCheckItemId dolu) ama mağaza/tarih
                // doğrulaması başarısız oldu — bu satır "eksik" (formu yok) değil, zaten "Mağaza
                // Uyuşmazlığı"/"Tarih Uyuşmazlığı" olarak ayrıca ve daha spesifik şekilde raporlanıyor.
                // Sadece form no hiç okunamadıysa/Excel'de bulunamadıysa (checkItemId yok) mağaza
                // "eksik" adayı olarak kalır.
                if (error.ProgressPaymentCheckItemId.HasValue)
                {
                    var addressedItem = checkItems.FirstOrDefault(i => i.Id == error.ProgressPaymentCheckItemId.Value);
                    var addressedKey = addressedItem != null ? TextNormalizationHelper.StoreKey(addressedItem.StoreCode, addressedItem.StoreName) : null;
                    if (!string.IsNullOrEmpty(addressedKey)) matchedStoreKeys.Add(addressedKey);
                }
                continue;
            }

            var first = matched![0];
            var key = TextNormalizationHelper.StoreKey(first.StoreCode, first.StoreName);
            if (!string.IsNullOrEmpty(key)) matchedStoreKeys.Add(key);
        }

        var eksikMagazalar = excelStoreKeys
            .Where(x => !matchedStoreKeys.Contains(x.Key))
            .GroupBy(x => x.Key)
            .Select(g => (StoreLabel: g.First().Item.StoreName ?? g.First().Item.StoreCode ?? "Bilinmeyen Mağaza", Items: g.Select(x => x.Item).ToList()))
            .ToList();

        var fazlaYabanciMagazalar = formStoreKeys
            .Where(kv => !excelStoreKeySet.Contains(kv.Key))
            .Select(kv => (StoreLabel: kv.Value.Label, Page: kv.Value.Page))
            .ToList();

        return new StoreReconciliationResult
        {
            ExceldekiMagazaCount = excelStoreKeySet.Count,
            FormlardaBulunanMagazaCount = formStoreKeys.Count,
            TamEslesenMagazaCount = matchedStoreKeys.Count,
            EksikMagazaCount = eksikMagazalar.Count,
            FazlaYabanciMagazaCount = fazlaYabanciMagazalar.Count,
            EslesmeyenFormCount = eslesmeyenFormlar.Count,
            EksikMagazalar = eksikMagazalar,
            FazlaYabanciMagazalar = fazlaYabanciMagazalar,
            EslesmeyenFormlar = eslesmeyenFormlar,
        };
    }

    /// <summary>Exceldeki mağazalardan hiçbir servis formu ile eşleşmeyenler için, o mağazaya ait
    /// her hakediş satırına export'ta görünecek bir "Mağaza Eşleşmesi / Eksik" notu üretir. Diğer
    /// tüm eşleşme sorunları (mağaza uyuşmazlığı, form bulunamadı vb.) zaten FormNumberMatcher
    /// tarafından ilgili hakediş satırına bağlı olarak kaydedilmiştir — burada tekrarlanmaz.</summary>
    public static async Task PersistMissingStoreRowsAsync(AppDbContext db, AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await db.AiComparisonResults
            .Where(r => r.JobId == job.Id && r.ItemType == AiComparisonItemType.StoreMatch)
            .ToListAsync(cancellationToken);
        db.AiComparisonResults.RemoveRange(existing);

        var pages = await db.AiDocumentPages
            .Where(p => p.JobId == job.Id && p.DocumentType == AiDocumentType.ServiceForm)
            .ToListAsync(cancellationToken);
        var checkItems = await db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        var result = Compute(job.Id, pages, checkItems);

        var newRows = result.EksikMagazalar.SelectMany(m => m.Items.Select(item => new AiComparisonResult
        {
            JobId = job.Id,
            StoreLabel = m.StoreLabel,
            VisitDate = item.VisitDate,
            ProgressPaymentCheckItemId = item.Id,
            ItemType = AiComparisonItemType.StoreMatch,
            Description = "Mağaza Eşleşmesi",
            FormValue = "—",
            HakedisValue = m.StoreLabel,
            Status = AiComparisonStatus.Eksik,
            Explanation = "Bu mağazaya ait hakediş kaydı bulunmaktadır ancak karşılığında servis formu yüklenmemiş/eşleştirilememiştir.",
            CreatedAt = DateTime.Now,
        }));

        db.AiComparisonResults.AddRange(newRows);
        await db.SaveChangesAsync(cancellationToken);
    }
}
