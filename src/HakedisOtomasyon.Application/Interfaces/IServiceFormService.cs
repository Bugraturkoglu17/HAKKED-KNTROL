using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Application.Interfaces;

public interface IServiceFormService
{
    Task<List<ServiceFormDto>> GetByClaimIdAsync(int claimId);
    Task<ServiceFormDto?> GetByIdAsync(int id);
    Task<ServiceFormDto> AddFormAsync(int claimId, string filePath, string originalFileName);
    Task<ServiceFormDto> AddBlankFormAsync(int claimId);
    Task UpdateStoreAsync(int formId, string storeCode);

    /// <summary>Servis formunun tarihini ve (bakım) form numarasını kaydeder — Soğutma Bakım &amp; Onarım çıktısındaki
    /// TARİH ve BAKIM FORM NO sütunları.</summary>
    Task UpdateFormInfoAsync(int formId, DateTime? formDate, string? formNumber);
    Task UpdateStatusAsync(int formId, ServiceFormStatus status);
    Task SaveItemsAsync(int formId, List<ServiceItemDto> items);
    Task DeleteAsync(int id);
    Task<List<ServiceFormDto>> GetIncompleteFormsAsync(int claimId);

    /// <summary>
    /// Soğutma Glikol hakedişi: her forma glikol kalemini OTOMATİK ekler (servis ücreti yoktur), mevcut glikol
    /// kalemlerini güncel fiyat/EUR kuruyla yeniler. Miktarlara dokunmaz.
    /// </summary>
    Task EnsureGlycolItemsAsync(int claimId);

    /// <summary>
    /// Soğutma Glikol hakedişi: bir form satırının kullanıcı girişlerini kaydeder — mağaza, form tarihi, form no
    /// ve glikol miktarı (kullanıcının düzenleyebildiği tek kalem alanı). Eksiksizse form "Tamamlandı" olur.
    /// </summary>
    Task<ServiceFormDto> SaveGlycolRowAsync(int formId, int? storeId, DateTime? formDate, string? formNumber, decimal quantity);

    /// <summary>
    /// Soğutma Glikol hakedişi: bir form satırını hakediş tablosundan çıkarır — mağaza, tarih, form no ve miktar temizlenir,
    /// form "Bekliyor" durumuna döner (yüklenen dosya ve glikol kalemi silinmez; form yeniden doldurulabilir).
    /// </summary>
    Task<ServiceFormDto> ClearGlycolRowAsync(int formId);

    /// <summary>
    /// Soğutma Glikol hakedişi: yüklenen bir dosyadan (örn. çok sayfalı bir PDF) YENİ bir kayıt ekler. Dosyanın henüz boş olan
    /// yer tutucu satırı varsa onu kullanır, yoksa aynı dosyaya bağlı yeni bir satır oluşturur — bir dosyadan istenen sayıda
    /// kayıt girilebilir. Dosyanın kendisi kopyalanmaz.
    /// </summary>
    Task<ServiceFormDto> AddGlycolEntryAsync(int sourceFormId, int? storeId, DateTime? formDate, string? formNumber, decimal quantity);

    /// <summary>
    /// Soğutma Glikol hakedişi: bir kaydı siler. Aynı dosyada başka kayıt varsa satır tamamen kaldırılır; dosyanın son kaydıysa
    /// satır boşaltılıp "Bekliyor"a döner (dosya hakedişte kalır). Satır kaldırıldıysa null döner.
    /// </summary>
    Task<ServiceFormDto?> RemoveGlycolRowAsync(int formId);
}
