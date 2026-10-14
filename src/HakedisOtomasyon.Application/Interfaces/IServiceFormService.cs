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
    /// <remarks><paramref name="pageNumber"/>: çok sayfalı PDF'de formun sayfası (1 tabanlı); verilmezse kayıtlı sayfa değişmez.</remarks>
    Task<ServiceFormDto> SaveGlycolRowAsync(int formId, int? storeId, DateTime? formDate, string? formNumber, decimal quantity, int? priceItemId = null,
        int? pageNumber = null);

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
    /// <remarks><paramref name="priceItemId"/>: yalnız Gaz Kullanım hakedişinde — satırın gaz türü (fiyat listesindeki gaz kalemi).
    /// Glikol hakedişinde yok sayılır (glikol kalemi otomatik).</remarks>
    Task<ServiceFormDto> AddGlycolEntryAsync(int sourceFormId, int? storeId, DateTime? formDate, string? formNumber, decimal quantity, int? priceItemId = null,
        int? pageNumber = null);

    /// <summary>
    /// Soğutma Glikol hakedişi: bir kaydı siler. Aynı dosyada başka kayıt varsa satır tamamen kaldırılır; dosyanın son kaydıysa
    /// satır boşaltılıp "Bekliyor"a döner (dosya hakedişte kalır). Satır kaldırıldıysa null döner.
    /// </summary>
    Task<ServiceFormDto?> RemoveGlycolRowAsync(int formId);

    /// <summary>
    /// Soğutma Bakım &amp; Onarım: yüklenen bir dosyadaki servis formuna (mağaza + tarih + bakım form no) bir malzeme/işçilik
    /// kalemi ekler. O form için kayıt yoksa oluşturur (dosyanın boş yer tutucusunu kullanır ya da aynı dosyaya bağlı yeni kayıt açar)
    /// ve mağazanın iline göre şehir içi/dışı servis bedelini OTOMATİK ekler. Aynı kalem formda zaten varsa miktarı artırır.
    /// <paramref name="priceItemId"/> boşsa yalnızca form kaydı + servis bedeli oluşturulur.
    /// </summary>
    /// <remarks><paramref name="pageNumber"/>: çok sayfalı PDF'de formun sayfası (1 tabanlı) — satıra tıklanınca PDF bu sayfadan açılır.</remarks>
    Task<MaintenanceEntryResult> AddMaintenanceItemAsync(int sourceFormId, int storeId, DateTime formDate, string formNumber,
        int? priceItemId, decimal quantity, int? pageNumber = null);

    /// <summary>
    /// Soğutma Kompresör Değişim: forma, fiyat listesinde OLMAYAN hakedişe özel bir kalem ekler (kompresör marka/modeli; liste
    /// fiyatı, iskontolu liste fiyatı ya da FATURA + %). Kalem genel fiyat listesine EKLENMEZ. Fatura bazlı kalemde fatura kaydı
    /// (dosyasıyla) forma bağlanır. Form kaydı ve otomatik servis bedeli <see cref="AddMaintenanceItemAsync"/> ile aynı kurallarla oluşur.
    /// </summary>
    Task<MaintenanceEntryResult> AddCustomMaintenanceItemAsync(int sourceFormId, int storeId, DateTime formDate, string formNumber,
        CustomClaimItemInput input, int? pageNumber = null);

    /// <summary>
    /// Soğutma Bakım &amp; Onarım: bir form kaydının mağaza/tarih/bakım form no bilgisini ve (verilmişse) bir kaleminin miktarını
    /// günceller. Mağazanın ili değiştiyse servis bedeli şehir içi ↔ dışı olarak değişir.
    /// </summary>
    Task<ServiceFormDto> UpdateMaintenanceEntryAsync(int formId, int storeId, DateTime formDate, string formNumber, int? itemId, decimal? quantity);

    /// <summary>
    /// Soğutma Bakım &amp; Onarım: bir kalemi siler. Formda başka kalem kalmadıysa form kaydı da kalkar (dosyanın son kaydıysa
    /// boşaltılıp "Bekliyor"a döner — dosya hakedişte kalır). Form kaydı kaldırıldıysa null döner.
    /// </summary>
    Task<ServiceFormDto?> RemoveMaintenanceItemAsync(int itemId);
}
