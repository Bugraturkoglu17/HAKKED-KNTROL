using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Infrastructure.Data;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

public class AiUsageTracker : IAiUsageTracker
{
    private readonly AppDbContext _db;

    public AiUsageTracker(AppDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(int jobId, int? pageId, AiTokenUsageDto usage)
    {
        _db.AiUsageLogs.Add(new AiUsageLog
        {
            JobId = jobId,
            PageId = pageId,
            Model = usage.Model,
            InputTokens = usage.InputTokens,
            CachedInputTokens = usage.CachedInputTokens,
            OutputTokens = usage.OutputTokens,
            ReasoningTokens = usage.ReasoningTokens,
            RequestedAt = DateTime.Now,
        });
        await _db.SaveChangesAsync();
    }
}
