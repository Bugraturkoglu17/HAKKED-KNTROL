using Microsoft.Win32;
using SogutmaHakedisKontrol.Application.Interfaces;

namespace SogutmaHakedisKontrol.Web;

public class WpfFilePickerService : IFilePickerService
{
    public Task<string?> PickExcelFileAsync()
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync<string?>(() =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Excel Dosyaları (*.xlsx;*.xls)|*.xlsx;*.xls|Tüm Dosyalar (*.*)|*.*",
                Multiselect = false,
                CheckFileExists = true
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }).Task;
    }
}
