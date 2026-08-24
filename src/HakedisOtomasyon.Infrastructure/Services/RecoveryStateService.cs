namespace HakedisOtomasyon.Infrastructure.Services;

/// <summary>
/// Singleton servis — uygulama açılışında kurtarma gerekip gerekmediğini
/// App.xaml.cs → MainLayout.razor arasında taşır.
/// </summary>
public sealed class RecoveryStateService
{
    public bool RecoveryNeeded { get; set; }
    public Application.DTOs.DataRecoveryScanResult? ScanResult { get; set; }

    /// <summary>
    /// Otomatik kurtarma yapıldıysa sonucu burada sakla (Snackbar gösterimi için).
    /// </summary>
    public Application.DTOs.DataRecoveryResult? AutoRestoreResult { get; set; }

    /// <summary>
    /// pending-restore.json işlenerek tam yedekten geri yükleme yapıldıysa true.
    /// MainLayout bunu snackbar ile kullanıcıya bildirir.
    /// </summary>
    public bool RestoredFromPendingBackup { get; set; }
}
