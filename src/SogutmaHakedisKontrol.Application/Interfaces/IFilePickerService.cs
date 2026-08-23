namespace SogutmaHakedisKontrol.Application.Interfaces;

public interface IFilePickerService
{
    /// <summary>Tek bir Excel dosyası seçer. İptal edilirse null döner.</summary>
    Task<string?> PickExcelFileAsync();
}
