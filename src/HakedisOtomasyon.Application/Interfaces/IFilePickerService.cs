namespace HakedisOtomasyon.Application.Interfaces;

public interface IFilePickerService
{
    /// <summary>Tek bir Excel dosyası seçer. İptal edilirse null döner.</summary>
    Task<string?> PickExcelFileAsync();

    /// <summary>Birden fazla dosya seçer.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string filter, bool multiselect = false);

    /// <summary>Kaydetme dialogu açar. İptal edilirse null döner.</summary>
    Task<string?> PickSaveFileAsync(string filter, string defaultFileName);

    /// <summary>PDF/JPG/PNG servis formu dosyaları seçer (çoklu seçim).</summary>
    Task<IReadOnlyList<string>> PickServiceFormFilesAsync();

    /// <summary>PDF/JPG/PNG fatura dosyası seçer (tekli seçim).</summary>
    Task<IReadOnlyList<string>> PickInvoiceFilesAsync();

    /// <summary>Excel kaydet dialogu açar. İptal edilirse null döner.</summary>
    Task<string?> PickSaveExcelFileAsync(string defaultFileName);

    /// <summary>Klasör seçme dialogu açar. İptal edilirse null döner.</summary>
    Task<string?> PickFolderAsync(string title = "Klasör Seçin");
}
