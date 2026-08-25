namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>OpenAI API çağrısı başına kullanım kaydı — maliyet takibi altyapısı (UI'sı ileride).</summary>
public class AiUsageLog
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public int? PageId { get; set; }

    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int ReasoningTokens { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.Now;
}
