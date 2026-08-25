using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>
/// OpenAI Responses API üzerinden tek bir sayfa görselini yapılandırılmış (Structured Outputs) veriye dönüştürür.
/// API anahtarı yalnızca bu servisin içinde (backend/Infrastructure), ortam değişkeninden okunur — asla UI'ya geçmez.
/// </summary>
public interface IAiVisionClient
{
    bool IsConfigured { get; }

    /// <summary>extraInstruction verilirse, seçilen hakediş kategorisine özel yönerge genel sistem talimatına eklenir.</summary>
    Task<AiVisionCallResultDto> AnalyzePageAsync(byte[] pageImagePng, string? extraInstruction = null, CancellationToken cancellationToken = default);
}
