using System.Collections.Concurrent;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>
/// Gerçek OpenAI çağrısı yapmadan, hangi sayfanın analiz edildiğini görsel baytındaki
/// sayfa indeksinden (bkz. FakePdfPageRasterizer) okuyup önceden tanımlı bir sonuç döner.
/// Eşzamanlı çalıştırmada bile hangi çağrının hangi sayfaya ait olduğu deterministic kalır.
/// </summary>
internal class FakeAiVisionClient : IAiVisionClient
{
    private readonly Func<int, byte, AiVisionCallResultDto> _resolve;
    private readonly ConcurrentDictionary<(int, byte), int> _callCountByPage = new();

    public bool IsConfigured => true;
    public int CallCountForPage(int pageIndex, byte source = 0) => _callCountByPage.GetValueOrDefault((pageIndex, source));

    /// <summary>resolve(pageIndex içindeki-PDF'e-göre-0-tabanlı, sourceMarker) — sourceMarker,
    /// FakePdfPageRasterizer'a verilen pdfBytes[0] değeridir (servis formu ve bakım formu PDF'lerini ayırt eder).</summary>
    public FakeAiVisionClient(Func<int, byte, AiVisionCallResultDto> resolve) => _resolve = resolve;

    /// <summary>Kaynak ayrımı gerekmeyen basit senaryolar için (tek PDF).</summary>
    public FakeAiVisionClient(Func<int, AiVisionCallResultDto> resolve) : this((idx, _) => resolve(idx)) { }

    public Task<AiVisionCallResultDto> AnalyzePageAsync(byte[] pageImagePng, string? extraInstruction = null, CancellationToken cancellationToken = default)
    {
        var pageIndex = pageImagePng[0];
        var source = pageImagePng[1];
        _callCountByPage.AddOrUpdate((pageIndex, source), 1, (_, c) => c + 1);
        return Task.FromResult(_resolve(pageIndex, source));
    }
}

/// <summary>Gerçek PDF işlemeden, istenen sayfa sayısı kadar görsel döner. Her görselin ilk baytı
/// kendi 0 tabanlı sayfa indeksi, ikinci baytı ise verilen pdfBytes[0] (kaynak PDF işareti) olur —
/// böylece sahte AI istemcisi hem hangi sayfanın hem hangi PDF'in (servis/bakım) analiz edildiğini bilir.</summary>
internal class FakePdfPageRasterizer : IPdfPageRasterizer
{
    private readonly int _pageCount;
    public FakePdfPageRasterizer(int pageCount) => _pageCount = pageCount;

    public List<byte[]> RasterizeToPngPages(byte[] pdfBytes, int dpi = 220)
    {
        var sourceMarker = pdfBytes.Length > 0 ? pdfBytes[0] : (byte)0;
        return Enumerable.Range(0, _pageCount).Select(i => new byte[] { (byte)i, sourceMarker, 0 }).ToList();
    }

    public int GetPageCount(byte[] pdfBytes) => _pageCount;
}

internal class FakeAppPathService : IAppPathService
{
    public string DataRootPath { get; } = Path.Combine(Path.GetTempPath(), "sogutma_test_data_" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(DataRootPath, "test.db");

    public FakeAppPathService() => Directory.CreateDirectory(DataRootPath);
}
