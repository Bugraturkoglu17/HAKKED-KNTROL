using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>
/// OpenAI Responses API üzerinden tek bir sayfa görselini yapılandırılmış (Structured Outputs) veriye dönüştürür.
/// API anahtarı yalnızca bu servisin içinde (backend/Infrastructure), ortam değişkeninden okunur — asla UI'ya geçmez.
/// </summary>
public interface IAiVisionClient
{
    bool IsConfigured { get; }

    Task<AiVisionCallResultDto> AnalyzePageAsync(byte[] pageImagePng, CancellationToken cancellationToken = default);
}
