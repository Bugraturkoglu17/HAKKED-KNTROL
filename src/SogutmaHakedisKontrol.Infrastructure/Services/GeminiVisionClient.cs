using System.Text;
using System.Text.Json;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Google Gemini API istemcisi — bulut tabanlı, güçlü bir vision modeliyle sayfa görsellerini
/// yapılandırılmış veriye çevirir. API anahtarı yalnızca ortam değişkeninden (GEMINI_API_KEY)
/// okunur, hiçbir zaman loglanmaz. Aynı IAiVisionClient sözleşmesini uygular; AiAnalysisPipelineService
/// hangi sağlayıcının kullanıldığını bilmez.
/// </summary>
public class GeminiVisionClient : IAiVisionClient
{
    // ConnectTimeout kısa tutulur: DNS/TCP/TLS bağlantısı hiç kurulamıyorsa (ağ hatası, kurumsal
    // proxy engeli vb.) 3 dakika beklemeden hızlıca hata dönülür. Bağlantı kurulup Gemini yanıt
    // üretmeye başladıysa (gerçek analiz) toplam Timeout süresince beklenmeye devam edilir.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    // Hata metninde API anahtarı sorgu parametresi olarak yer alabilir — asla loglama/döndürme.
    private static readonly System.Text.RegularExpressions.Regex KeyPattern =
        new("\"key\"\\s*:\\s*\"[^\"]*\"", System.Text.RegularExpressions.RegexOptions.Compiled);
    private const string KeyReplacement = "\"key\":\"[GİZLİ]\"";

    private readonly string? _apiKey;
    private readonly string _model;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public GeminiVisionClient()
    {
        _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        _model = Environment.GetEnvironmentVariable("GEMINI_MODEL") is { Length: > 0 } m ? m : "gemini-3.5-flash-lite";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
    public string ProviderLabel => $"Google Gemini — Model: {_model}";

    public async Task<AiVisionCallResultDto> AnalyzePageAsync(byte[] pageImagePng, string? extraInstruction = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new AiVisionCallResultDto { Success = false, ErrorMessage = "GEMINI_API_KEY tanımlı değil." };

        try
        {
            var systemInstruction = string.IsNullOrWhiteSpace(extraInstruction)
                ? AiVisionSchemas.SystemInstruction
                : AiVisionSchemas.SystemInstruction + "\n\n--- SEÇİLEN HAKEDİŞ KATEGORİSİ İÇİN ÖNCELİKLİ ALANLAR ---\n" + extraInstruction;

            // Gemini'nin responseSchema formatı (OpenAPI alt kümesi) OpenAI'ın "strict" JSON Schema'sıyla
            // birebir uyumlu değil (ör. type:["string","null"] desteklenmiyor). Sağlam çözüm: şemayı
            // sistem talimatına metin olarak gömüp yalnızca responseMimeType=application/json ile
            // geçerli JSON üretimini zorlamak — Gemini bu şekilde de şemaya oldukça sadık kalıyor.
            var fullSystemInstruction = systemInstruction +
                "\n\n── ZORUNLU JSON ŞEMASI ──────────────────────────────────────────────\n" +
                "Yanıtın TAMAMI, aşağıdaki JSON şemasına birebir uymalı (fazladan alan ekleme, " +
                "eksik alan bırakma, sadece bu şemadaki alanları kullan):\n" + AiVisionSchemas.JsonSchema;

            var requestBody = new
            {
                systemInstruction = new
                {
                    parts = new object[] { new { text = fullSystemInstruction } },
                },
                contents = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = "Bu görsel bir soğutma servis/bakım formu sayfasıdır. Sistem talimatına göre analiz et ve yalnızca JSON şemasına uygun sonuç döndür." },
                            new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(pageImagePng) } },
                        },
                    },
                },
                generationConfig = new
                {
                    temperature = 0,
                    responseMimeType = "application/json",
                },
            };

            var requestJson = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
            using var response = await Http.PostAsync(url, content, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Hata metninde API key sorgu parametresi olarak yer alabilir — asla loglama/döndürme.
                var scrubbed = KeyPattern.Replace(responseText, KeyReplacement);
                return new AiVisionCallResultDto
                {
                    Success = false,
                    ErrorMessage = $"Gemini HTTP {(int)response.StatusCode}: {scrubbed}",
                    RetryAfter = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? ParseRetryDelay(responseText)
                        : null,
                };
            }

            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            var outputText = root
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(outputText))
                return new AiVisionCallResultDto { Success = false, ErrorMessage = "Model boş yanıt döndürdü." };

            AiPageExtractionDto? extraction;
            try
            {
                extraction = JsonSerializer.Deserialize<AiPageExtractionDto>(outputText, _jsonOptions);
            }
            catch (JsonException jex)
            {
                return new AiVisionCallResultDto { Success = false, ErrorMessage = $"Yanıt ayrıştırılamadı: {jex.Message}", RawJson = outputText };
            }

            if (extraction is null)
                return new AiVisionCallResultDto { Success = false, ErrorMessage = "Yanıt ayrıştırılamadı.", RawJson = outputText };

            AiTokenUsageDto? usage = null;
            if (root.TryGetProperty("usageMetadata", out var usageEl))
            {
                usage = new AiTokenUsageDto
                {
                    Model = _model,
                    InputTokens = usageEl.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0,
                    OutputTokens = usageEl.TryGetProperty("candidatesTokenCount", out var ct) ? ct.GetInt32() : 0,
                };
            }

            return new AiVisionCallResultDto { Success = true, Extraction = extraction, RawJson = outputText, Usage = usage };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiVisionCallResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Gemini'nin 429 yanıtındaki "retryDelay": "52s" (ya da "52.46s") alanını ayrıştırır —
    /// hız sınırı (ücretsiz katman: gemini-3.5-flash-lite için 15 istek/dakika) aşıldığında API'nin
    /// kendi önerdiği bekleme süresini kullanmak, sabit kısa yeniden deneme aralıklarıyla aynı dakikalık
    /// pencereye tekrar tekrar çarpıp tüm denemeleri boşa harcamaktan çok daha güvenilirdir.</summary>
    private static TimeSpan? ParseRetryDelay(string errorResponseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorResponseJson);
            if (!doc.RootElement.TryGetProperty("error", out var error)) return null;
            if (!error.TryGetProperty("details", out var details)) return null;
            foreach (var detail in details.EnumerateArray())
            {
                if (!detail.TryGetProperty("retryDelay", out var retryDelayEl)) continue;
                var raw = retryDelayEl.GetString();
                if (string.IsNullOrEmpty(raw) || !raw.EndsWith('s')) continue;
                if (double.TryParse(raw[..^1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    return TimeSpan.FromSeconds(seconds);
            }
        }
        catch (JsonException)
        {
            // Hata gövdesi beklenen şemada değil — sessizce null döner, pipeline sabit gecikmeye düşer.
        }
        return null;
    }
}
