using System.Text.Json;
using OpenAI;
using OpenAI.Responses;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// OpenAI Responses API istemcisi — GPT-5.5 ile sayfa görsellerini yapılandırılmış veriye çevirir.
/// API anahtarı yalnızca ortam değişkeninden (OPENAI_API_KEY) okunur, hiçbir zaman loglanmaz veya
/// UI'ya geçirilmez. Sistem talimatı her çağrıda otomatik uygulanır (bkz. AiVisionSchemas).
/// Tek bir çağrı = tek bir deneme; retry mantığı orkestrasyon katmanındadır (bkz. AiAnalysisPipelineService).
/// </summary>
public class OpenAiVisionClient : IAiVisionClient
{
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly ResponseReasoningEffortLevel _reasoningEffort;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public OpenAiVisionClient()
    {
        _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _model = Environment.GetEnvironmentVariable("OPENAI_MODEL") is { Length: > 0 } m ? m : "gpt-5.5";
        _reasoningEffort = (Environment.GetEnvironmentVariable("OPENAI_REASONING_EFFORT") ?? "medium").ToLowerInvariant() switch
        {
            "low" => ResponseReasoningEffortLevel.Low,
            "high" => ResponseReasoningEffortLevel.High,
            "minimal" => ResponseReasoningEffortLevel.Minimal,
            _ => ResponseReasoningEffortLevel.Medium,
        };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
    public string ProviderLabel => $"OpenAI — Model: {_model}";

    public async Task<AiVisionCallResultDto> AnalyzePageAsync(byte[] pageImagePng, string? extraInstruction = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new AiVisionCallResultDto { Success = false, ErrorMessage = "OPENAI_API_KEY tanımlı değil." };

        try
        {
            var client = new OpenAIClient(_apiKey!);
            var responses = client.GetResponsesClient();

            var userContent = new List<ResponseContentPart>
            {
                ResponseContentPart.CreateInputTextPart(
                    "Bu görsel bir soğutma servis/bakım formu sayfasıdır. Sistem talimatına göre analiz et " +
                    "ve yalnızca JSON şemasına uygun sonuç döndür."),
                ResponseContentPart.CreateInputImagePart(BinaryData.FromBytes(pageImagePng, "image/jpeg"), ResponseImageDetailLevel.High),
            };

            var systemInstruction = string.IsNullOrWhiteSpace(extraInstruction)
                ? AiVisionSchemas.SystemInstruction
                : AiVisionSchemas.SystemInstruction + "\n\n--- SEÇİLEN HAKEDİŞ KATEGORİSİ İÇİN ÖNCELİKLİ ALANLAR ---\n" + extraInstruction;

            var options = new CreateResponseOptions(_model, new[]
            {
                ResponseItem.CreateDeveloperMessageItem(systemInstruction),
                ResponseItem.CreateUserMessageItem(userContent),
            })
            {
                TextOptions = new ResponseTextOptions
                {
                    TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                        AiVisionSchemas.SchemaName,
                        BinaryData.FromString(AiVisionSchemas.JsonSchema),
                        jsonSchemaFormatDescription: null,
                        jsonSchemaIsStrict: true),
                },
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = _reasoningEffort,
                },
            };

            var apiResult = await responses.CreateResponseAsync(options, cancellationToken);
            ResponseResult response = apiResult.Value;

            var outputText = ExtractOutputText(response);
            if (string.IsNullOrWhiteSpace(outputText))
                return new AiVisionCallResultDto { Success = false, ErrorMessage = "Model boş yanıt döndürdü." };

            var extraction = JsonSerializer.Deserialize<AiPageExtractionDto>(outputText, _jsonOptions);
            if (extraction is null)
                return new AiVisionCallResultDto { Success = false, ErrorMessage = "Yanıt ayrıştırılamadı.", RawJson = outputText };

            return new AiVisionCallResultDto
            {
                Success = true,
                Extraction = extraction,
                RawJson = outputText,
                Usage = ToUsageDto(response),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Hata mesajında API key'in yer almadığından emin ol (SDK istisnaları anahtarı içermez).
            return new AiVisionCallResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static string ExtractOutputText(ResponseResult response)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var outputItem in response.OutputItems)
        {
            if (outputItem is not MessageResponseItem msg || msg.Role != MessageRole.Assistant) continue;
            foreach (var part in msg.Content)
            {
                if (part.Kind == ResponseContentPartKind.OutputText && !string.IsNullOrEmpty(part.Text))
                    sb.Append(part.Text);
            }
        }
        return sb.ToString();
    }

    private AiTokenUsageDto? ToUsageDto(ResponseResult response)
    {
        var usage = response.Usage;
        if (usage is null) return null;
        return new AiTokenUsageDto
        {
            Model = _model,
            InputTokens = usage.InputTokenCount,
            CachedInputTokens = usage.InputTokenDetails?.CachedTokenCount ?? 0,
            OutputTokens = usage.OutputTokenCount,
            ReasoningTokens = usage.OutputTokenDetails?.ReasoningTokenCount ?? 0,
        };
    }
}
