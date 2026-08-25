using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Domain.Entities;

/// <summary>
/// Bir AiAnalysisJob'a servis formu tarafında birden fazla PDF yüklenebilir (bkz. spec: "Servis_Formlari_1.pdf",
/// "Servis_Formlari_2.pdf"). Sayfa numaralandırması SourceKind bazında global ve arttırılarak devam eder
/// (dosya 1: 1..N1, dosya 2: N1+1..N1+N2, ...) — bu yüzden AiDocumentPage şemasına dokunmadan, bir sayfanın
/// hangi orijinal dosyaya ait olduğu PageOffset/PageCount aralığından çözülür.
/// </summary>
public class AiSourceDocument
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public AiDocumentSource SourceKind { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int PageOffset { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public AiAnalysisJob Job { get; set; } = null!;
}
