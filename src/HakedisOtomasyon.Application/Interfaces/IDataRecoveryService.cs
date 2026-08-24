using HakedisOtomasyon.Application.DTOs;

namespace HakedisOtomasyon.Application.Interfaces;

/// <summary>
/// Mağaza / Fiyat Kalemleri verilerini korunan konuma yedekler ve
/// yeni boş veritabanı oluştuğunda otomatik/manuel geri yükleme yapar.
/// </summary>
public interface IDataRecoveryService
{
    // ── Korunan yedek (AppData\Local\ServisHakedis\ProtectedMasterData) ──────

    /// <summary>
    /// Mevcut DB'deki Stores, PriceItems ve PriceAliases tablolarını
    /// korunan JSON yedeklere kaydeder. Her başarılı import sonrası çağrılmalı.
    /// </summary>
    Task SaveProtectedSnapshotAsync();

    /// <summary>
    /// Korunan konumda kayıtlı yedek verisi var mı?
    /// </summary>
    bool HasProtectedSnapshot { get; }

    // ── Tarama ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Desteklenen tüm konumlarda eski veritabanı ve korunan veri arar.
    /// </summary>
    Task<DataRecoveryScanResult> ScanForRecoverySourcesAsync();

    // ── Geri yükleme ────────────────────────────────────────────────────────

    /// <summary>
    /// Korunan JSON yedekten mevcut DB'ye geri yükler.
    /// </summary>
    Task<DataRecoveryResult> RestoreFromProtectedDataAsync(
        bool includeStores, bool includePriceItems, bool includeAliases);

    /// <summary>
    /// Dışarıdan seçilen eski hakedis.db dosyasından mevcut DB'ye geri yükler.
    /// </summary>
    Task<DataRecoveryResult> RestoreFromDatabaseAsync(
        string dbPath, bool includeStores, bool includePriceItems, bool includeAliases);

    // ── Durum ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mevcut DB'de Stores VEYA PriceItems tablosu boş mu?
    /// </summary>
    Task<bool> TablesAreEmptyAsync();
}
