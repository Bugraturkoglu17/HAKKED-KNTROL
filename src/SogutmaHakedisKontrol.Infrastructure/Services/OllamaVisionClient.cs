using System.Text;
using System.Text.Json;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// Ollama üzerinden yerel çalışan bir vision modeliyle (varsayılan: qwen2.5vl) sayfa görsellerini
/// yapılandırılmış veriye çevirir. Hiçbir veri internete çıkmaz — kurumsal ağ engellemesi (Infoblox)
/// bu istemciyi etkilemez, çünkü istek hiç dışarı gitmiyor. OpenAiVisionClient ile aynı IAiVisionClient
/// sözleşmesini uygular; AiAnalysisPipelineService hangi sağlayıcının kullanıldığını bilmez.
/// </summary>
public class OllamaVisionClient : IAiVisionClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonElement SchemaElement = JsonDocument.Parse(AiVisionSchemas.JsonSchema).RootElement.Clone();

    private readonly string _baseUrl;
    private readonly string _model;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public OllamaVisionClient()
    {
        _baseUrl = (Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") is { Length: > 0 } b ? b : "http://localhost:11434").TrimEnd('/');
        _model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") is { Length: > 0 } m ? m : "qwen2.5vl:7b";
    }

    public bool IsConfigured => true; // yerel servis, API anahtarı gerekmez
    public string ProviderLabel => $"Ollama (yerel) — Model: {_model}";

    public async Task<AiVisionCallResultDto> AnalyzePageAsync(byte[] pageImagePng, string? extraInstruction = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var systemInstruction = string.IsNullOrWhiteSpace(extraInstruction)
                ? AiVisionSchemas.SystemInstruction
                : AiVisionSchemas.SystemInstruction + "\n\n--- SEÇİLEN HAKEDİŞ KATEGORİSİ İÇİN ÖNCELİKLİ ALANLAR ---\n" + extraInstruction;

            var requestBody = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = systemInstruction },
                    new
                    {
                        role = "user",
                        content = "Bu görsel bir soğutma servis/bakım formu sayfasıdır. Sistem talimatına göre analiz et " +
                                  "ve yalnızca JSON şemasına uygun sonuç döndür.",
                        images = new[] { Convert.ToBase64String(pageImagePng) },
                    },
                },
                format = SchemaElement,
                stream = false,
                options = new { temperature = 0 },
            };

            var requestJson = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync($"{_baseUrl}/api/chat", content, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new AiVisionCallResultDto { Success = false, ErrorMessage = $"Ollama HTTP {(int)response.StatusCode}: {responseText}" };

            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("message", out var messageEl) || !messageEl.TryGetProperty("content", out var contentEl))
                return new AiVisionCallResultDto { Success = false, ErrorMessage = "Ollama yanıtında 'message.content' bulunamadı.", RawJson = responseText };

            var outputText = contentEl.GetString();
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

            var usage = new AiTokenUsageDto
            {
                Model = _model,
                InputTokens = root.TryGetProperty("prompt_eval_count", out var pe) ? pe.GetInt32() : 0,
                OutputTokens = root.TryGetProperty("eval_count", out var ec) ? ec.GetInt32() : 0,
            };

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
}
