using Microsoft.EntityFrameworkCore;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Kategoriden bağımsız, tüm hakediş türlerinde geçerli FORM bazlı mutabakat (dosya adı tarihi nedenlerle
/// "StoreFormReconciliationBuilder" kalmıştır, sınıf adı ve mantığı artık form granülaritesindedir).
/// Ana referans hakediş Excelidir: Excel'deki her form numarası "beklenen kayıt"tır — bir mağazanın birden
/// fazla formu olabilir, HERHANGİ biri yüklenmemişse o form eksik sayılır (mağaza bazında değil).
///
/// İki bağımsız yarı:
///  - PersistMissingFormRowsAsync: YAZMA — kategori stratejisi çalıştıktan SONRA, hiç ele alınmamış
///    (hiçbir AiComparisonResult'ta referans edilmeyen) form-no gruplarına "Form Eksik" satırı ekler.
///  - ComputeSummaryAsync: OKUMA — TAMAMEN persisted veriden hesaplar, FormNumberMatcher'ı yeniden
///    ÇAĞIRMAZ. Bu yüzden kullanıcının "Onay ver" ile düzelttiği eski hatalar (bkz. AiComparisonOverride)
///    özet panelinde otomatik güncel görünür — ayrı bir "override farkında" mantığa gerek yoktur, çünkü
///    okuduğu satırlar zaten pipeline'da ApplyOverridesAsync/FormNumberMatcher'ın override-kurtarması
///    sayesinde güncel durumu yansıtıyor.
/// </summary>
internal static class StoreFormReconciliationBuilder
{
    public sealed class FormReconciliationSummary
    {
        public int BeklenenFormSayisi { get; init; }
        public int EslesenFormSayisi { get; init; }
        public int EksikFormSayisi => EksikFormlar.Count;
        public int FazlaFormSayisi { get; init; }
        public int MukerrerZiyaretSayisi => MukerrerZiyaretMesajlari.Count;

        public List<(string FormNo, string StoreLabel, DateTime? VisitDate)> EksikFormlar { get; init; } = new();
        public List<string> MukerrerZiyaretMesajlari { get; init; } = new();
        public List<(string StoreLabel, string Description, string Explanation)> DigerSorunlar { get; init; } = new();
    }

    /// <summary>Hiçbir üyesi (form-no grubundaki hakediş kalemlerinden hiçbiri) herhangi bir
    /// AiComparisonResult'ta referans edilmemiş form-no gruplarına, gruptaki her kalem için bir
    /// "Form Eksik" satırı ekler. Bir kalemin "ele alınmış" sayılması için Status'ün Uygun olması
    /// gerekmez — Mağaza/Tarih uyuşmazlığı bile kendi spesifik notuyla zaten raporlandığı için
    /// ele alınmış sayılır (aksi halde aynı satır hem "Mağaza Uyuşmazlığı" hem "Form Eksik" olarak
    /// mükerrer görünürdü).</summary>
    public static async Task PersistMissingFormRowsAsync(AppDbContext db, AiAnalysisJob job, CancellationToken cancellationToken)
    {
        var existing = await db.AiComparisonResults
            .Where(r => r.JobId == job.Id && r.ItemType == AiComparisonItemType.StoreMatch)
            .ToListAsync(cancellationToken);
        db.AiComparisonResults.RemoveRange(existing);

        var checkItems = await db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == job.ProgressPaymentCheckId)
            .ToListAsync(cancellationToken);

        var addressedItemIds = await db.AiComparisonResults
            .Where(r => r.JobId == job.Id && r.ProgressPaymentCheckItemId != null)
            .Select(r => r.ProgressPaymentCheckItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var addressedSet = addressedItemIds.ToHashSet();

        var formGroups = checkItems
            .Where(i => !string.IsNullOrWhiteSpace(i.MaintenanceFormNo))
            .GroupBy(i => TextNormalizationHelper.NormalizeCode(i.MaintenanceFormNo!));

        var newRows = new List<AiComparisonResult>();
        foreach (var g in formGroups)
        {
            if (g.Any(i => addressedSet.Contains(i.Id))) continue; // en az bir kalem ele alınmışsa form "eksik" değil

            var items = g.ToList();
            var first = items[0];
            var storeLabel = first.StoreName ?? first.StoreCode ?? "Bilinmeyen Mağaza";
            foreach (var item in items)
            {
                newRows.Add(new AiComparisonResult
                {
                    JobId = job.Id,
                    StoreLabel = storeLabel,
                    VisitDate = item.VisitDate,
                    ProgressPaymentCheckItemId = item.Id,
                    ItemType = AiComparisonItemType.StoreMatch,
                    Description = "Form Eksik",
                    FormValue = "—",
                    HakedisValue = first.MaintenanceFormNo,
                    Status = AiComparisonStatus.Eksik,
                    Explanation = $"\"{first.MaintenanceFormNo}\" numaralı form için hakedişte {storeLabel} mağazasına ait kayıt " +
                                  "bulunmaktadır ancak karşılığında servis formu yüklenmemiş/eşleştirilememiştir.",
                    CreatedAt = DateTime.Now,
                });
            }
        }

        db.AiComparisonResults.AddRange(newRows);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<FormReconciliationSummary> ComputeSummaryAsync(
        AppDbContext db, int jobId, int progressPaymentCheckId, CancellationToken cancellationToken)
    {
        var checkItems = await db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == progressPaymentCheckId)
            .ToListAsync(cancellationToken);
        var results = await db.AiComparisonResults
            .Where(r => r.JobId == jobId)
            .ToListAsync(cancellationToken);

        var expectedFormNos = checkItems
            .Where(i => !string.IsNullOrWhiteSpace(i.MaintenanceFormNo))
            .Select(i => TextNormalizationHelper.NormalizeCode(i.MaintenanceFormNo!))
            .Distinct()
            .ToList();

        var eksikRows = results
            .Where(r => r.ItemType == AiComparisonItemType.StoreMatch && r.Description == "Form Eksik" && r.Status != AiComparisonStatus.Uygun)
            .ToList();
        var eksikFormNoSet = eksikRows
            .Select(r => TextNormalizationHelper.NormalizeCode(r.HakedisValue ?? string.Empty))
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet();

        var eksikFormlar = eksikRows
            .GroupBy(r => TextNormalizationHelper.NormalizeCode(r.HakedisValue ?? string.Empty))
            .Select(g => g.First())
            .Select(r => (FormNo: r.HakedisValue ?? "-", StoreLabel: r.StoreLabel, VisitDate: r.VisitDate))
            .ToList();

        var fazlaFormSayisi = results.Count(r => r.Description == "Form Hakedişte Bulunamadı" && r.Status != AiComparisonStatus.Uygun);

        var mukerrerZiyaretMesajlari = checkItems
            .Where(i => !string.IsNullOrWhiteSpace(i.MaintenanceFormNo) && i.VisitDate.HasValue)
            .GroupBy(i => (Store: TextNormalizationHelper.StoreKey(i.StoreCode, i.StoreName), Date: i.VisitDate!.Value.Date))
            .Where(g => !string.IsNullOrEmpty(g.Key.Store))
            .Select(g => new
            {
                g.Key.Date,
                Label = g.First().StoreName ?? g.First().StoreCode ?? "Bilinmeyen Mağaza",
                FormNos = g.Select(i => i.MaintenanceFormNo!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .Where(x => x.FormNos.Count > 1)
            .Select(x => $"{x.Label} — {x.Date:dd.MM.yyyy} için birden fazla servis formu bulunmaktadır (Form {string.Join(", ", x.FormNos)}).")
            .ToList();

        // "Form Hakedişte Bulunamadı" ayrıca ve yalnızca sayı olarak bildirilir (bkz. FazlaFormSayisi) —
        // burada tekrar tekil satır olarak listelenmez.
        var digerSorunlar = results
            .Where(r => FormNumberMatcher.GateErrorDescriptions.Contains(r.Description)
                        && r.Description != "Form Hakedişte Bulunamadı" && r.Status != AiComparisonStatus.Uygun)
            .Select(r => (r.StoreLabel, r.Description, r.Explanation))
            .ToList();

        return new FormReconciliationSummary
        {
            BeklenenFormSayisi = expectedFormNos.Count,
            EslesenFormSayisi = expectedFormNos.Count(f => !eksikFormNoSet.Contains(f)),
            FazlaFormSayisi = fazlaFormSayisi,
            EksikFormlar = eksikFormlar,
            MukerrerZiyaretMesajlari = mukerrerZiyaretMesajlari,
            DigerSorunlar = digerSorunlar,
        };
    }
}
