using System.Xml.Linq;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// TCMB XML web servisi üzerinden USD kuru çeker ve SQLite önbelleğinde saklar.
/// Hafta sonu / tatil günlerinde en yakın önceki iş gününün kuruna geri çekilir.
/// </summary>
public class TcmbExchangeRateService : IExchangeRateService
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;

    private const string CurrencyCode = "USD";
    private const string TcmbBaseUrl  = "https://www.tcmb.gov.tr/kurlar/";
    private const int    MaxLookback  = 10;

    public TcmbExchangeRateService(AppDbContext db, HttpClient http)
    {
        _db   = db;
        _http = http;
    }

    public async Task<ExchangeRateResult> GetUsdRateAsync(
        DateTime date,
        ExchangeRateType type = ExchangeRateType.ForexSelling)
    {
        for (int i = 0; i <= MaxLookback; i++)
        {
            var candidate = date.Date.AddDays(-i);

            // ── 1. Önbellekte var mı? ───────────────────────────────────────
            var cached = await _db.ExchangeRateCaches
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.CurrencyCode == CurrencyCode
                                       && r.RateDate     == candidate);
            if (cached is not null)
            {
                var cachedRate = type == ExchangeRateType.ForexBuying
                    ? cached.ForexBuying
                    : cached.ForexSelling;
                return Success(date, candidate, cachedRate, cached.Source, i > 0);
            }

            // ── 2. TCMB'den çek ─────────────────────────────────────────────
            var url = candidate.Date == DateTime.Today
                ? $"{TcmbBaseUrl}today.xml"
                : $"{TcmbBaseUrl}{candidate:yyyyMM}/{candidate:ddMMyyyy}.xml";

            try
            {
                using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var xmlText      = await _http.GetStringAsync(url, cts.Token);
                var (buying, selling) = ParseUsdFromXml(xmlText);

                if (buying <= 0 || selling <= 0) continue; // bülten içinde USD yoksa sonraki güne geç

                // ── Önbelleğe yaz ───────────────────────────────────────────
                var cacheEntry = new ExchangeRateCache
                {
                    CurrencyCode = CurrencyCode,
                    RateDate     = candidate,
                    ForexBuying  = buying,
                    ForexSelling = selling,
                    Source       = "TCMB",
                    CreatedAt    = DateTime.Now,
                    UpdatedAt    = DateTime.Now
                };
                _db.ExchangeRateCaches.Add(cacheEntry);
                try { await _db.SaveChangesAsync(); }
                catch { /* unique constraint ihlali — başka thread zaten yazmış olabilir */ }

                var liveRate = type == ExchangeRateType.ForexBuying ? buying : selling;
                return Success(date, candidate, liveRate, "TCMB", i > 0);
            }
            catch (TaskCanceledException)
            {
                // Zaman aşımı — bir önceki güne geç
            }
            catch (HttpRequestException)
            {
                // Ağ hatası — bir önceki güne geç
            }
            catch (Exception)
            {
                // XML parse veya beklenmedik hata — bir önceki güne geç
            }
        }

        return new ExchangeRateResult
        {
            RequestedDate = date,
            ActualDate    = date,
            Rate          = 0,
            CurrencyCode  = CurrencyCode,
            Source        = "TCMB",
            IsFallbackDate = false,
            Message       = $"Son {MaxLookback} gün içinde TCMB'den USD kuru alınamadı. " +
                            "Lütfen internet bağlantınızı kontrol edin veya kuru manuel girin."
        };
    }

    // ── Yardımcı metodlar ────────────────────────────────────────────────────

    private static ExchangeRateResult Success(
        DateTime requested, DateTime actual, decimal rate, string source, bool isFallback)
        => new()
        {
            RequestedDate  = requested,
            ActualDate     = actual,
            Rate           = rate,
            CurrencyCode   = CurrencyCode,
            Source         = source,
            IsFallbackDate = isFallback
        };

    /// <summary>
    /// TCMB XML'inden USD ForexBuying ve ForexSelling değerlerini okur.
    /// </summary>
    private static (decimal Buying, decimal Selling) ParseUsdFromXml(string xml)
    {
        var doc   = XDocument.Parse(xml);
        var usdEl = doc.Descendants("Currency")
                       .FirstOrDefault(el =>
                           (string?)el.Attribute("CurrencyCode") == "USD");

        if (usdEl is null) return (0, 0);

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        decimal.TryParse((string?)usdEl.Element("ForexBuying"),
                         System.Globalization.NumberStyles.Number, culture, out var buying);
        decimal.TryParse((string?)usdEl.Element("ForexSelling"),
                         System.Globalization.NumberStyles.Number, culture, out var selling);
        return (buying, selling);
    }
}
