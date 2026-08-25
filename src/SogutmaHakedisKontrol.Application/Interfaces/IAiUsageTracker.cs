using SogutmaHakedisKontrol.Application.DTOs;

namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IAiUsageTracker
{
    Task LogAsync(int jobId, int? pageId, AiTokenUsageDto usage);
}
