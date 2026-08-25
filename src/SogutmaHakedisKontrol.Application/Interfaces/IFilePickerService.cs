namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IFilePickerService
{
    /// <summary>Tek bir Excel dosyası seçer. İptal edilirse null döner.</summary>
    Task<string?> PickExcelFileAsync();

    /// <summary>Servis/bakım formu belgesi seçer (PDF veya görsel). İptal edilirse null döner.</summary>
    Task<string?> PickDocumentFileAsync();
}
