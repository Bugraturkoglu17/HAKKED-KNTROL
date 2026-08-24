using HakedisOtomasyon.Application.Interfaces;
using Microsoft.Win32;

namespace HakedisOtomasyon.Web;

public class WpfFilePickerService : IFilePickerService
{
    private const string FormFilter =
        "Desteklenen Dosyalar (*.pdf;*.jpg;*.jpeg;*.png)|*.pdf;*.jpg;*.jpeg;*.png|Tüm Dosyalar (*.*)|*.*";

    public Task<string?> PickExcelFileAsync()
    {
        return PickSingleFileAsync("Excel Dosyaları (*.xlsx;*.xls)|*.xlsx;*.xls|Tüm Dosyalar (*.*)|*.*");
    }

    public Task<IReadOnlyList<string>> PickServiceFormFilesAsync()
    {
        return PickFilesAsync(FormFilter, multiselect: true);
    }

    public Task<IReadOnlyList<string>> PickInvoiceFilesAsync()
    {
        return PickFilesAsync(FormFilter, multiselect: false);
    }

    public Task<string?> PickSaveExcelFileAsync(string defaultFileName)
    {
        return PickSaveFileAsync("Excel Dosyaları (*.xlsx)|*.xlsx", defaultFileName);
    }

    public Task<IReadOnlyList<string>> PickFilesAsync(string filter, bool multiselect = false)
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync<IReadOnlyList<string>>(() =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = filter,
                Multiselect = multiselect,
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
                return (IReadOnlyList<string>)dlg.FileNames;
            return Array.Empty<string>();
        }).Task;
    }

    public Task<string?> PickSaveFileAsync(string filter, string defaultFileName)
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync<string?>(() =>
        {
            var dlg = new SaveFileDialog
            {
                Filter = filter,
                FileName = defaultFileName
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }).Task;
    }

    private Task<string?> PickSingleFileAsync(string filter)
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync<string?>(() =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = filter,
                Multiselect = false,
                CheckFileExists = true
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }).Task;
    }

    public Task<string?> PickFolderAsync(string title = "Klasör Seçin")
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync<string?>(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = title,
                Multiselect = false,
            };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }).Task;
    }
}
