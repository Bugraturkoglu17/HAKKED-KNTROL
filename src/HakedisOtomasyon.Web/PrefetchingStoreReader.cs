using System.Collections.Concurrent;
using System.IO;
using HakedisOtomasyon.Application;

namespace HakedisOtomasyon.Web;

/// <summary>
/// Herhangi bir <see cref="IHandwrittenStoreReader"/>'ın (Azure / Gemini) önüne konan önbellek + arka planda önden okuma katmanı.
/// Kullanıcı bir sayfada "Otomatik doldur"a basınca o sayfa hemen okunur; sonraki sayfalar arka planda sırayla okunup önbelleğe
/// alınır ve o sayfaya gelince sonuç anında döner. Kullanıcı başka bir sayfaya geçip "Otomatik doldur" derse:
///   • o sayfa öne geçer (sırada bekliyorsa hemen okunur, okunuyorsa sonucu beklenir, okunmuşsa önbellekten gelir),
///   • sürmekte olan arka plan Azure/Gemini isteği iptal edilir (istek hakkı bu sayfaya kalır) ve sayfa sonra yeniden sıraya alınır,
///   • kullanıcı okuması sürerken arka plan yeni istek başlatmaz,
///   • arka plan sırası yeni sayfadan itibaren yeniden düzenlenir; önceki sonuçlar önbellekte kalır.
/// Arka plan okumaları TEK TEK yapılır (istek sınırlarına takılmamak için). Hatalı sonuç önbelleğe alınmaz.
/// </summary>
public sealed class PrefetchingStoreReader : IHandwrittenStoreReader
{
    private sealed class Entry
    {
        public readonly TaskCompletionSource<HandwrittenStoreResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int State;     // 0: sırada, 1: başladı (kullanıcı ya da arka plan sahiplendi)
        public int Waiters;   // bu sonucu bekleyen kullanıcı okumaları (varsa arka plan okuması iptal edilmez)
    }

    private sealed record QueueItem(string Key, Entry Entry, string Path, int Page);

    private const int MaxCacheEntries = 300;
    private readonly IHandwrittenStoreReader _inner;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly object _lock = new();
    private readonly List<QueueItem> _queue = new();
    private bool _workerRunning;
    private int _foreground;                // sürmekte olan kullanıcı okumaları
    private string? _focusPath;
    private int _focusPage;
    private (QueueItem Item, CancellationTokenSource Cts)? _current;   // şu an Azure'da süren arka plan okuması

    public PrefetchingStoreReader(IHandwrittenStoreReader inner) => _inner = inner;

    public bool IsConfigured => _inner.IsConfigured;

    /// <summary>Anahtar dosya yoluna + boyuta + değişme zamanına bağlıdır; dosya değişirse eski sonuç kullanılmaz.</summary>
    private static string CacheKey(string path, int page)
    {
        var info = new FileInfo(path);
        return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{page}";
    }

    private async Task<HandwrittenStoreResult> RunAsync(string key, Entry entry, string path, int page)
    {
        HandwrittenStoreResult result;
        try { result = await _inner.ReadAsync(path, page, CancellationToken.None); }
        catch (Exception ex) { result = new(null, null, $"Okuma başarısız: {ex.Message}"); }
        if (result.Error is not null) _cache.TryRemove(key, out _);   // hata önbelleğe alınmaz: sonra yeniden denenir
        entry.Result.TrySetResult(result);
        return result;
    }

    /// <summary>Arka plan okuması: kullanıcı başka bir sayfaya odaklanırsa iptal edilir ve sayfa sıraya geri alınır.</summary>
    private async Task PrefetchOneAsync(QueueItem item)
    {
        using var cts = new CancellationTokenSource();
        lock (_lock) _current = (item, cts);
        HandwrittenStoreResult result;
        try { result = await _inner.ReadAsync(item.Path, item.Page, cts.Token); }
        catch (OperationCanceledException) { result = new(null, null, "Önden okuma iptal edildi."); }
        catch (Exception ex) { result = new(null, null, $"Okuma başarısız: {ex.Message}"); }
        lock (_lock) _current = null;

        if (result.Error is not null) _cache.TryRemove(item.Key, out _);
        item.Entry.Result.TrySetResult(result);   // bu sayfayı bekleyen kullanıcı olursa hata görür ve doğrudan yeniden okur
        if (cts.IsCancellationRequested && result.Error is not null)
        {
            lock (_lock)
            {
                var again = new Entry();
                if (_cache.TryAdd(item.Key, again)) _queue.Add(new QueueItem(item.Key, again, item.Path, item.Page));
            }
        }
    }

    public async Task<HandwrittenStoreResult> ReadAsync(string absoluteFilePath, int pageIndex = 0, CancellationToken ct = default)
    {
        if (!IsConfigured || !File.Exists(absoluteFilePath)) return await _inner.ReadAsync(absoluteFilePath, pageIndex, ct);
        if (_cache.Count > MaxCacheEntries) _cache.Clear();

        var key = CacheKey(absoluteFilePath, pageIndex);
        lock (_lock)
        {
            _focusPath = absoluteFilePath; _focusPage = pageIndex;   // arka plan sırası bu sayfadan itibaren sürer
            // başka bir sayfayı okuyan arka plan isteği varsa iptal edilir: Azure'un istek hakkı bu sayfaya kalsın
            if (_current is { } c && c.Item.Key != key && Volatile.Read(ref c.Item.Entry.Waiters) == 0) c.Cts.Cancel();
        }
        Interlocked.Increment(ref _foreground);
        try
        {
            var entry = _cache.GetOrAdd(key, _ => new Entry());
            // sırada bekleyen (ya da yeni) kaydı kullanıcı sahiplenir ve hemen okur; başlamış/bitmişse sonucu beklenir
            if (Interlocked.CompareExchange(ref entry.State, 1, 0) == 0)
                return await RunAsync(key, entry, absoluteFilePath, pageIndex).WaitAsync(ct);

            Interlocked.Increment(ref entry.Waiters);
            HandwrittenStoreResult result;
            try { result = await entry.Result.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref entry.Waiters); }
            // arka plandaki okuma hata verdiyse (istek sınırı, iptal vb.) kullanıcı için bir kez daha doğrudan denenir
            return result.Error is null ? result : await _inner.ReadAsync(absoluteFilePath, pageIndex, ct);
        }
        finally { Interlocked.Decrement(ref _foreground); }
    }

    public void Prefetch(string absoluteFilePath, IReadOnlyList<int> pageIndexes)
    {
        if (!IsConfigured || !File.Exists(absoluteFilePath)) return;
        lock (_lock)
        {
            foreach (var page in pageIndexes)
            {
                var key = CacheKey(absoluteFilePath, page);
                var entry = new Entry();
                if (!_cache.TryAdd(key, entry)) continue;   // zaten okundu / okunuyor / sırada
                _queue.Add(new QueueItem(key, entry, absoluteFilePath, page));
            }
            if (_queue.Count == 0 || _workerRunning) return;
            _workerRunning = true;
        }
        _ = Task.Run(WorkerAsync);
    }

    /// <summary>Odak sayfadan ileri doğru en yakın sayfa önce; sonra odağın gerisindekiler; başka dosyalar en sonda.</summary>
    private int Rank(QueueItem item)
        => item.Path != _focusPath ? 20000
         : item.Page >= _focusPage ? item.Page - _focusPage
         : 10000 + (_focusPage - item.Page);

    private async Task WorkerAsync()
    {
        while (true)
        {
            QueueItem item;
            lock (_lock)
            {
                _queue.RemoveAll(q => Volatile.Read(ref q.Entry.State) != 0);   // kullanıcı kendisi okumuş olanlar
                if (_queue.Count == 0) { _workerRunning = false; return; }
                item = _queue.OrderBy(Rank).First();
                _queue.Remove(item);
            }
            while (Volatile.Read(ref _foreground) > 0) await Task.Delay(250);   // kullanıcının okuması önce
            if (Interlocked.CompareExchange(ref item.Entry.State, 1, 0) != 0) continue;
            await PrefetchOneAsync(item);
        }
    }
}
