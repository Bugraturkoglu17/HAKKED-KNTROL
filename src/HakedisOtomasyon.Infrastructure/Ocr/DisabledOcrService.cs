using HakedisOtomasyon.Application.Interfaces;

namespace HakedisOtomasyon.Infrastructure.Ocr;

/// <summary>
/// OCR devre dışı — ilk sürümde OCR kullanılmaz.
/// IOcrService arayüzünü boş implementasyonla karşılar.
/// İleride TesseractOcrService veya AzureOcrService ile değiştirilebilir.
/// </summary>
public class DisabledOcrService : IOcrService
{
    public bool IsEnabled => false;

    public Task<string?> ExtractTextAsync(string filePath) =>
        Task.FromResult<string?>(null);

    public Task<string?> ExtractStoreCodeAsync(string filePath) =>
        Task.FromResult<string?>(null);
}
