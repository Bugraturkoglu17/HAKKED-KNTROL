using System.Windows;
using System.Windows.Media;

namespace HakedisOtomasyon.Web.Licensing;

public partial class LicenseActivationWindow : Window
{
    public bool ActivationSucceeded { get; private set; } = false;

    public LicenseActivationWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtDeviceCode.Text = MachineIdService.GetShortDeviceCode();
        TxtFirmaAdi.Focus();
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(TxtDeviceCode.Text);
        BtnCopy.Content = "✔  Kopyalandı";

        // 2 saniye sonra butonu sıfırla
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (s, _) =>
        {
            BtnCopy.Content = "📋  Kopyala";
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
        };
        timer.Start();
    }

    private void BtnActivate_Click(object sender, RoutedEventArgs e)
    {
        HideMessage();

        var firmaAdi = TxtFirmaAdi.Text?.Trim() ?? string.Empty;
        var activationCode = TxtActivationCode.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(firmaAdi))
        {
            ShowError("Lütfen firma adını girin.");
            TxtFirmaAdi.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(activationCode))
        {
            ShowError("Lütfen aktivasyon kodunu girin.");
            TxtActivationCode.Focus();
            return;
        }

        BtnActivate.IsEnabled = false;
        BtnActivate.Content = "Doğrulanıyor...";

        var (success, error) = LicenseService.ActivateAndSave(activationCode, firmaAdi);

        BtnActivate.IsEnabled = true;
        BtnActivate.Content = "Aktive Et";

        if (success)
        {
            ShowSuccess("Lisans başarıyla etkinleştirildi.");

            // 1.5 saniye sonra pencereyi kapat ve ana uygulamayı aç
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            timer.Tick += (s, _) =>
            {
                ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                ActivationSucceeded = true;
                DialogResult = true;
                Close();
            };
            timer.Start();
        }
        else
        {
            ShowError(error ?? "Aktivasyon başarısız.");
        }
    }

    private void ShowError(string message)
    {
        MessageBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0xEE));
        MessageBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        MessageBorder.BorderThickness = new Thickness(1);
        TxtMessage.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        TxtMessage.Text = message;
        MessageBorder.Visibility = Visibility.Visible;
    }

    private void ShowSuccess(string message)
    {
        MessageBorder.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
        MessageBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        MessageBorder.BorderThickness = new Thickness(1);
        TxtMessage.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
        TxtMessage.Text = message;
        MessageBorder.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageBorder.Visibility = Visibility.Collapsed;
    }
}
