namespace SogutmaHakedisKontrol.Application.DTOs;

/// <summary>
/// GPT'nin bir sayfa için döndürdüğü yapılandırılmış (Structured Outputs / JSON Schema) sonuç.
/// document_type alanı hem sınıflandırma hem çıkarımı tek çağrıda birleştirir.
/// Bu DTO'nun şekli AiVisionSchemas'taki JSON Schema ile birebir eşleşmelidir.
/// </summary>
public class AiPageExtractionDto
{
    public string DocumentType { get; set; } = "UNKNOWN"; // SUMMARY | SERVICE_FORM | PERIODIC_MAINTENANCE_FORM | UNKNOWN
    public string? FormNumber { get; set; }
    public AiStoreCandidateDto? Store { get; set; }
    public string? ServiceDate { get; set; }        // yyyy-MM-dd, yalnızca SERVICE_FORM
    public string? MaintenanceDate { get; set; }     // yyyy-MM-dd, yalnızca PERIODIC_MAINTENANCE_FORM
    public string? DescriptionRaw { get; set; }
    public string? WorkPerformedRaw { get; set; }
    public decimal? FormTotalHours { get; set; }
    public List<AiEmployeeExtractionDto> Employees { get; set; } = new();
    public List<AiMaterialExtractionDto> Materials { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool RequiresManualReview { get; set; }
}

public class AiStoreCandidateDto
{
    public string? CodeRaw { get; set; }
    public string? NameRaw { get; set; }
    public decimal Confidence { get; set; }
}

public class AiEmployeeExtractionDto
{
    public string? NameRaw { get; set; }
    public string? StartTime { get; set; }   // "10:00"
    public string? EndTime { get; set; }
    public decimal Confidence { get; set; }
}

public class AiMaterialExtractionDto
{
    public string RawName { get; set; } = string.Empty;
    public string? NormalizedName { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal Confidence { get; set; }
    public bool RequiresManualReview { get; set; }
}

/// <summary>Bir OpenAI çağrısının token kullanımı.</summary>
public class AiTokenUsageDto
{
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int ReasoningTokens { get; set; }
}

/// <summary>Tek bir vision çağrısının tam sonucu (ayrıştırılmış veri + kullanım).</summary>
public class AiVisionCallResultDto
{
    public bool Success { get; set; }
    public AiPageExtractionDto? Extraction { get; set; }
    public string? RawJson { get; set; }
    public AiTokenUsageDto? Usage { get; set; }
    public string? ErrorMessage { get; set; }
}
