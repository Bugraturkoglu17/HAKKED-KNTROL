namespace SogutmaHakedisKontrol.Domain.Enums;

public enum AiJobStatus
{
    Pending = 0,
    Splitting = 1,
    Analyzing = 2,
    Matching = 3,
    Comparing = 4,
    Completed = 5,
    CompletedWithErrors = 6,
    Failed = 7,
}
