namespace HakedisOtomasyon.Application.Interfaces;

/// <summary>
/// OCR servisi için temel arayüz.
/// İlk sürümde aktif OCR kullanılmaz; DisabledOcrService bu arayüzü boş bırakır.
/// İleride TesseractOcrService veya AzureOcrService bu arayüzü implemente edebilir.
/// </summary>
public interface IOcrService
{
    bool IsEnabled { get; }
    Task<string?> ExtractTextAsync(string filePath);
    Task<string?> ExtractStoreCodeAsync(string filePath);
}
