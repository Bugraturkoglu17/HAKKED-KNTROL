using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>
/// Bir AI belge analizi job'ının, hakediş Excel satırlarıyla nasıl karşılaştırılacağını tanımlar.
/// Kategoriye göre farklı karşılaştırma mantığı (ör. Gaz Kullanım için kg bazlı miktar kontrolü)
/// bu stratejilerin registry üzerinden seçilmesiyle sağlanır — dağınık if/else yerine tek dispatch noktası.
/// </summary>
public interface ICategoryComparisonStrategy
{
    /// <summary>Bu stratejinin ait olduğu kategori; varsayılan/genel strateji için null.</summary>
    HakedisCategory? Category { get; }

    /// <summary>Bu kategori TEK bir kontrol kalemi üzerinden çalışıyorsa (ör. Glikol Kullanım → "Glikol
    /// Miktarı (kg)", Gaz Kullanım → "Gaz Miktarı (kg)") o kalemin adı; birden çok kalemli (malzeme
    /// listesi, adam-saat vb.) kategorilerde null. Eksik form satırlarını (bkz.
    /// StoreFormReconciliationBuilder.PersistMissingFormRowsAsync) kategoriye özgü, tanınabilir bir
    /// etiketle göstermek için kullanılır — generik "Form Eksik" yerine.</summary>
    string? SingleItemLabel { get; }

    /// <summary>Job'a ait önceki sonuçları temizleyip yeniden hesaplar (idempotent).</summary>
    Task BuildAsync(AiAnalysisJob job, CancellationToken cancellationToken);
}

public interface ICategoryComparisonStrategyRegistry
{
    /// <summary>Kategoriye özel strateji varsa onu, yoksa genel/varsayılan stratejiyi döner.</summary>
    ICategoryComparisonStrategy Get(HakedisCategory? category);
}
