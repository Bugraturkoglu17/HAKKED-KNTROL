namespace HakedisOtomasyon.Infrastructure.PdfProcessing;

/// <summary>
/// PDF motoruna (PDFium, PDFtoImage üzerinden) yapılan TÜM çağrılar bu kapıdan tek tek geçer. PDFium aynı anda birden çok
/// iş parçacığından çağrılmaya dayanıklı değildir: sayfa görüntüleyici çizim yaparken aynı anda sayfa sayısı ya da form no
/// okunursa motor kilitlenebiliyordu (çok sayfalı PDF'de ilk birkaç sayfadan sonra hiçbir sayfa gelmiyordu).
/// </summary>
public static class PdfiumGate
{
    private static readonly object Lock = new();

    public static T Run<T>(Func<T> work)
    {
        lock (Lock) return work();
    }
}
