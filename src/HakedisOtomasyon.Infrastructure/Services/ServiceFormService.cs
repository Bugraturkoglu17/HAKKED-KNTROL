using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.DTOs;
using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Domain.Enums;
using HakedisOtomasyon.Infrastructure.Data;
using HakedisOtomasyon.Infrastructure.FileStorage;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

public class ServiceFormService : IServiceFormService
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly ISettingsService _settings;
    private readonly IFileCleanupService _cleanup;

    public ServiceFormService(AppDbContext db, IFileStorageService fileStorage, ISettingsService settings, IFileCleanupService cleanup)
    {
        _db = db;
        _fileStorage = fileStorage;
        _settings = settings;
        _cleanup = cleanup;
    }

    public async Task<List<ServiceFormDto>> GetByClaimIdAsync(int claimId)
    {
        var forms = await _db.ServiceForms
            .Include(f => f.Store)
            .Include(f => f.ServiceItems)
            .Include(f => f.Invoices.Where(i => !i.IsArchived))
            .Where(f => f.ProgressClaimId == claimId && !f.IsArchived)
            .OrderBy(f => f.UploadedAt)
            .ToListAsync();

        return forms.Select(MapToDto).ToList();
    }

    public async Task<ServiceFormDto?> GetByIdAsync(int id)
    {
        var form = await _db.ServiceForms
            .Include(f => f.Store)
            .Include(f => f.ServiceItems.OrderBy(i => i.SortOrder))
            .Include(f => f.Invoices.Where(i => !i.IsArchived))
            .FirstOrDefaultAsync(f => f.Id == id);
        return form is null ? null : MapToDto(form);
    }

    public async Task<ServiceFormDto> AddFormAsync(int claimId, string filePath, string originalFileName)
    {
        // Soğutma Glikol hakedişinde servis ücreti YOKTUR (glikol kalemi sonradan otomatik eklenir)
        var ownerClaim = await _db.ProgressClaims.FindAsync(claimId);
        bool glycolClaim = ownerClaim is not null && GlycolClaim.IsGlycol(ownerClaim.Discipline, ownerClaim.CoolingCategory);
        // Soğutma Bakım & Onarım: sabit "Servis Ücreti" yerine mağazanın iline göre şehir içi/dışı servis bedeli
        // mağaza seçilince eklenir (bkz. MaintenanceClaim.ApplyServiceFee).
        bool maintenanceClaim = ownerClaim is not null && MaintenanceClaim.UsesMaterialScreen(ownerClaim.Discipline, ownerClaim.CoolingCategory);
        bool noDefaultFee = glycolClaim || maintenanceClaim;
        var defaultFee = noDefaultFee ? 0m : await _settings.GetDecimalAsync("DefaultServiceFee", 4550m);

        // Hash + duplicate detection
        var absPath = _fileStorage.GetAbsolutePath(filePath);
        string? fileHash = null;
        long fileSize = 0;
        if (File.Exists(absPath))
        {
            fileHash = await FileCleanupService.ComputeFileHashAsync(absPath);
            fileSize = new FileInfo(absPath).Length;

            var isDuplicate = await _db.ServiceForms
                .AnyAsync(f => f.ProgressClaimId == claimId
                               && f.FileHash == fileHash
                               && !f.IsArchived);
            if (isDuplicate)
            {
                // Yeni kopyalanan dosyayı sil, hata fırlat
                try { File.Delete(absPath); } catch { /* yoksay */ }
                throw new InvalidOperationException(
                    "Bu servis formu zaten bu hakedişe eklenmiş.");
            }
        }

        var entity = new ServiceForm
        {
            ProgressClaimId = claimId,
            OriginalFileName = originalFileName,
            FilePath = filePath,
            Status = ServiceFormStatus.Bekliyor,
            FileHash = fileHash,
            FileSize = fileSize
        };
        _db.ServiceForms.Add(entity);
        await _db.SaveChangesAsync();

        // Otomatik Servis Ücreti satırı ekle (Glikol ve Bakım & Onarım hakedişleri hariç)
        if (!noDefaultFee)
        {
            var defaultItem = new ServiceItem
            {
                ServiceFormId = entity.Id,
                Description = "Servis Ücreti",
                Quantity = 1,
                Unit = "Ad",
                MaterialPrice = 0,
                LaborPrice = defaultFee,
                UnitPrice = defaultFee,
                TotalPrice = defaultFee,
                SortOrder = 1
            };
            _db.ServiceItems.Add(defaultItem);
            await _db.SaveChangesAsync();
        }

        // Kaydedilen veriyi ilişkili tablolarla birlikte yeniden yükle
        var loaded = await _db.ServiceForms
            .Include(f => f.Store)
            .Include(f => f.ServiceItems.OrderBy(i => i.SortOrder))
            .Include(f => f.Invoices.Where(i => !i.IsArchived))
            .FirstAsync(f => f.Id == entity.Id);

        return MapToDto(loaded);
    }

    public async Task<ServiceFormDto> AddBlankFormAsync(int claimId)
    {
        var defaultFee = await _settings.GetDecimalAsync("DefaultServiceFee", 4550m);
        var blankOwner = await _db.ProgressClaims.FindAsync(claimId);
        bool blankMaintenance = blankOwner is not null && MaintenanceClaim.UsesMaterialScreen(blankOwner.Discipline, blankOwner.CoolingCategory);

        var entity = new ServiceForm
        {
            ProgressClaimId  = claimId,
            OriginalFileName = "Manuel Giriş",
            FilePath         = string.Empty,
            Status           = ServiceFormStatus.Isleniyor
        };
        _db.ServiceForms.Add(entity);
        await _db.SaveChangesAsync();

        var defaultItem = new ServiceItem
        {
            ServiceFormId = entity.Id,
            Description   = "Servis Ücreti",
            Quantity      = 1,
            Unit          = "Ad",
            MaterialPrice = 0,
            LaborPrice    = defaultFee,
            UnitPrice     = defaultFee,
            TotalPrice    = defaultFee,
            SortOrder     = 1
        };
        if (!blankMaintenance)   // Bakım & Onarım: servis bedeli mağaza seçilince ile göre eklenir
        {
            _db.ServiceItems.Add(defaultItem);
            await _db.SaveChangesAsync();
        }

        var loaded = await _db.ServiceForms
            .Include(f => f.Store)
            .Include(f => f.ServiceItems.OrderBy(i => i.SortOrder))
            .Include(f => f.Invoices.Where(i => !i.IsArchived))
            .FirstAsync(f => f.Id == entity.Id);

        return MapToDto(loaded);
    }

    public async Task UpdateStoreAsync(int formId, string storeCode)
    {
        var form = await _db.ServiceForms.FindAsync(formId)
            ?? throw new InvalidOperationException($"Form bulunamadı: {formId}");

        // Mağaza, hakedişin KENDİ disiplininde aranır — aynı kod Yangın'da da Soğutma'da da bulunabilir.
        var claimDiscipline = await _db.ProgressClaims.Where(c => c.Id == form.ProgressClaimId)
            .Select(c => c.Discipline).FirstAsync();
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Code == storeCode.Trim().ToUpper() && s.Discipline == claimDiscipline)
            ?? throw new InvalidOperationException($"Mağaza kodu bulunamadı: {storeCode}");

        form.StoreId = store.Id;
        if (form.Status == ServiceFormStatus.Bekliyor)
            form.Status = ServiceFormStatus.Isleniyor;

        await _db.SaveChangesAsync();
    }

    public async Task UpdateStatusAsync(int formId, ServiceFormStatus status)
    {
        var form = await _db.ServiceForms.FindAsync(formId);
        if (form is null) return;
        form.Status = status;
        await _db.SaveChangesAsync();
    }

    public async Task SaveItemsAsync(int formId, List<ServiceItemDto> items)
    {
        var existing = await _db.ServiceItems.Where(i => i.ServiceFormId == formId).ToListAsync();
        _db.ServiceItems.RemoveRange(existing);

        int order = 1;
        foreach (var dto in items)
        {
            _db.ServiceItems.Add(new ServiceItem
            {
                ServiceFormId = formId,
                Description = dto.Description,
                Quantity = dto.Quantity,
                Unit = dto.Unit,
                MaterialPrice = dto.MaterialPrice,
                LaborPrice = dto.LaborPrice,
                UnitPrice = dto.UnitPrice,
                TotalPrice = dto.TotalPrice,
                IsInvoiceBased = dto.IsInvoiceBased,
                InvoiceId = dto.InvoiceId,
                IsManualEntry = dto.IsManualEntry,
                IsAutoGenerated = dto.IsAutoGenerated,
                AutoGeneratedType = dto.AutoGeneratedType,
                AutoRuleTrigger = dto.AutoRuleTrigger,
                ExportDescription = dto.ExportDescription,
                ItemKey = dto.LocalId.ToString(),
                ParentItemKey = dto.ParentLocalId?.ToString(),
                IsCurrencyBased = dto.IsCurrencyBased,
                CurrencyCode = dto.CurrencyCode,
                ListPriceUsd = dto.ListPriceUsd,
                DiscountRate = dto.DiscountRate,
                DiscountedUsdPrice = dto.DiscountedUsdPrice,
                ExchangeRate = dto.ExchangeRate,
                ExchangeRateDate = dto.ExchangeRateDate,
                ExchangeRateActualDate = dto.ExchangeRateActualDate,
                ExchangeRateSource = dto.ExchangeRateSource,
                ForeignPrice = dto.ForeignPrice,
                ItemCode = dto.ItemCode,
                ItemType = dto.ItemType,
                SortOrder = order++
            });
        }

        var form = await _db.ServiceForms.FindAsync(formId);
        if (form is not null)
        {
            form.Status = items.Any() && form.StoreId.HasValue
                ? ServiceFormStatus.Tamamlandi
                : ServiceFormStatus.EksikBilgi;
        }

        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ //
    //  SOĞUTMA — GLİKOL KULLANIM HAKEDİŞİ
    // ------------------------------------------------------------------ //
    private async Task<(ProgressClaim Claim, PriceItem Price)> LoadGlycolContextAsync(int claimId)
    {
        var claim = await _db.ProgressClaims.FindAsync(claimId)
            ?? throw new InvalidOperationException($"Hakediş bulunamadı: {claimId}");
        if (!GlycolClaim.IsGlycol(claim.Discipline, claim.CoolingCategory))
            throw new InvalidOperationException("Bu hakediş Glikol Kullanım türünde değil.");

        var price = await FindGlycolPriceItemAsync()
            ?? throw new InvalidOperationException(
                $"Soğutma fiyat listesinde glikol kalemi (\"{GlycolClaim.PriceItemNamePrefix}\") bulunamadı. Önce Fiyat Listesi'ni kontrol edin.");
        if (price.ForeignPrice is > 0m && !EurPricing.IsValidRate(claim.EurRate))
            throw new InvalidOperationException("Glikol hakedişi için EUR kuru girilmemiş. Hakediş Bilgileri adımından EUR kurunu girin.");
        return (claim, price);
    }

    private async Task<PriceItem?> FindGlycolPriceItemAsync()
    {
        var candidates = await _db.PriceItems
            .Where(p => p.Discipline == MechanicalDiscipline.Cooling && p.IsActive
                     && (p.PozNo == GlycolClaim.FallbackItemCode || p.Description.Contains("MONOPROP")))
            .ToListAsync();
        return candidates.FirstOrDefault(p => GlycolClaim.IsGlycolPriceName(p.Description))
            ?? candidates.FirstOrDefault(p => p.PozNo == GlycolClaim.FallbackItemCode);
    }

    /// <summary>Glikol kalemine fiyat listesindeki güncel bilgiyi uygular (miktar korunur).</summary>
    private static void ApplyGlycolPricing(ServiceItem item, PriceItem price, ProgressClaim claim)
    {
        item.ItemCode = price.PozNo ?? GlycolClaim.FallbackItemCode;
        item.Description = GlycolClaim.CleanItemName(price.Description);
        item.Unit = string.IsNullOrWhiteSpace(price.Unit) ? "Ad" : price.Unit;
        item.LaborPrice = 0m; // Soğutma'da işçilik yok — tek "Birim Fiyat"

        if (price.ForeignPrice is { } eur && EurPricing.IsEur(price.CurrencyCode))
        {
            var rate = claim.EurRate!.Value;
            item.CurrencyCode = EurPricing.CurrencyCode;
            item.ForeignPrice = eur;
            item.ExchangeRate = rate;
            item.ExchangeRateDate = claim.EurRateDate;
            item.ExchangeRateActualDate = claim.EurRateDate;
            item.ExchangeRateSource = claim.EurRateSource ?? "Manuel";
            item.MaterialPrice = EurPricing.ToTl(eur, rate);
            item.UnitPrice = item.MaterialPrice;
            item.TotalPrice = EurPricing.TotalTl(item.Quantity, eur, rate);
        }
        else
        {
            item.CurrencyCode = null;
            item.ForeignPrice = null;
            item.ExchangeRate = null;
            item.ExchangeRateDate = null;
            item.ExchangeRateActualDate = null;
            item.ExchangeRateSource = null;
            item.MaterialPrice = price.MaterialPrice + price.LaborPrice;
            item.UnitPrice = item.MaterialPrice;
            item.TotalPrice = Math.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
        }
    }

    public async Task EnsureGlycolItemsAsync(int claimId)
    {
        var (claim, price) = await LoadGlycolContextAsync(claimId);
        var code = price.PozNo ?? GlycolClaim.FallbackItemCode;

        var forms = await _db.ServiceForms
            .Include(f => f.ServiceItems)
            .Where(f => f.ProgressClaimId == claimId && !f.IsArchived)
            .ToListAsync();

        foreach (var form in forms)
        {
            // Glikol hakedişinde servis ücreti yoktur: otomatik eklenmiş servis ücreti satırları kaldırılır.
            var serviceFees = form.ServiceItems
                .Where(i => i.Description == "Servis Ücreti" && i.ItemCode == null && !i.IsManualEntry)
                .ToList();
            if (serviceFees.Count > 0) _db.ServiceItems.RemoveRange(serviceFees);

            var glycol = form.ServiceItems.FirstOrDefault(i => i.ItemCode == code);
            if (glycol is null)
            {
                glycol = new ServiceItem
                {
                    ServiceFormId = form.Id,
                    Quantity = 0m,               // kullanıcı yalnızca miktarı girer
                    SortOrder = 1,
                    ItemKey = Guid.NewGuid().ToString(),
                };
                ApplyGlycolPricing(glycol, price, claim);
                _db.ServiceItems.Add(glycol);
            }
            else
            {
                ApplyGlycolPricing(glycol, price, claim);
            }
        }
        await _db.SaveChangesAsync();
    }

    public async Task<ServiceFormDto> SaveGlycolRowAsync(int formId, int? storeId, DateTime? formDate, string? formNumber, decimal quantity)
    {
        var form = await _db.ServiceForms.Include(f => f.ServiceItems).FirstOrDefaultAsync(f => f.Id == formId)
            ?? throw new InvalidOperationException($"Form bulunamadı: {formId}");
        var (claim, price) = await LoadGlycolContextAsync(form.ProgressClaimId);
        var code = price.PozNo ?? GlycolClaim.FallbackItemCode;

        if (storeId.HasValue)
        {
            // Yalnızca hakedişin kendi disiplinindeki (Soğutma) mağazalar seçilebilir
            var exists = await _db.Stores.AnyAsync(s => s.Id == storeId.Value && s.Discipline == claim.Discipline);
            if (!exists) throw new InvalidOperationException("Seçilen mağaza bu hakedişin disiplininde bulunamadı.");
        }
        form.StoreId = storeId;
        form.FormDate = formDate?.Date;
        form.FormNumber = string.IsNullOrWhiteSpace(formNumber) ? null : formNumber.Trim();

        var glycol = form.ServiceItems.FirstOrDefault(i => i.ItemCode == code);
        if (glycol is null)
        {
            glycol = new ServiceItem { ServiceFormId = form.Id, SortOrder = 1, ItemKey = Guid.NewGuid().ToString() };
            _db.ServiceItems.Add(glycol);
        }
        glycol.Quantity = Math.Max(0m, quantity);
        ApplyGlycolPricing(glycol, price, claim);

        bool complete = form.StoreId.HasValue && form.FormDate.HasValue
                        && !string.IsNullOrWhiteSpace(form.FormNumber) && glycol.Quantity > 0m;
        form.Status = complete ? ServiceFormStatus.Tamamlandi : ServiceFormStatus.EksikBilgi;

        await _db.SaveChangesAsync();
        return (await GetByIdAsync(formId))!;
    }

    public async Task<ServiceFormDto> AddGlycolEntryAsync(int sourceFormId, int? storeId, DateTime? formDate, string? formNumber, decimal quantity)
    {
        var source = await _db.ServiceForms.FirstOrDefaultAsync(f => f.Id == sourceFormId)
            ?? throw new InvalidOperationException($"Form bulunamadı: {sourceFormId}");
        await LoadGlycolContextAsync(source.ProgressClaimId); // glikol hakedişi + fiyat/kur doğrulaması

        var siblings = await _db.ServiceForms.Include(f => f.ServiceItems)
            .Where(f => f.ProgressClaimId == source.ProgressClaimId && f.FilePath == source.FilePath && !f.IsArchived)
            .ToListAsync();

        // Henüz hiçbir şey girilmemiş yer tutucu varsa onu kullan
        var target = siblings.FirstOrDefault(f => f.StoreId == null && f.FormDate == null
            && string.IsNullOrWhiteSpace(f.FormNumber) && !f.ServiceItems.Any(i => i.ItemCode != null && i.Quantity > 0m));

        if (target is null)
        {
            target = new ServiceForm
            {
                ProgressClaimId = source.ProgressClaimId,
                OriginalFileName = source.OriginalFileName,
                FilePath = source.FilePath,   // aynı dosya — kopyalanmaz
                FileSize = source.FileSize,
                FileHash = null,              // tekrar (duplicate) denetimi yalnızca yükleme sırasında yapılır
                UploadedAt = source.UploadedAt,
                Status = ServiceFormStatus.Bekliyor,
            };
            _db.ServiceForms.Add(target);
            await _db.SaveChangesAsync();
        }
        return await SaveGlycolRowAsync(target.Id, storeId, formDate, formNumber, quantity);
    }

    public async Task<ServiceFormDto?> RemoveGlycolRowAsync(int formId)
    {
        var form = await _db.ServiceForms.Include(f => f.ServiceItems).FirstOrDefaultAsync(f => f.Id == formId)
            ?? throw new InvalidOperationException($"Form bulunamadı: {formId}");

        var hasOtherEntries = await _db.ServiceForms.AnyAsync(f => f.Id != formId
            && f.ProgressClaimId == form.ProgressClaimId && f.FilePath == form.FilePath && !f.IsArchived);
        if (!hasOtherEntries)
            return await ClearGlycolRowAsync(formId);   // dosyanın son kaydı: dosya hakedişte kalır, satır boşalır

        _db.ServiceItems.RemoveRange(form.ServiceItems);
        _db.ServiceForms.Remove(form);                  // yalnızca bu kayıt; dosya ve diğer kayıtlar dokunulmaz
        await _db.SaveChangesAsync();
        return null;
    }

    // ─── Soğutma Bakım & Onarım ──────────────────────────────────────────────

    private async Task<ProgressClaim> LoadMaintenanceClaimAsync(int claimId)
    {
        var claim = await _db.ProgressClaims.FirstOrDefaultAsync(c => c.Id == claimId)
            ?? throw new InvalidOperationException($"Hakediş bulunamadı: {claimId}");
        if (!MaintenanceClaim.UsesMaterialScreen(claim.Discipline, claim.CoolingCategory))
            throw new InvalidOperationException("Bu hakediş Bakım & Onarım / Kompresör Değişim / Kısmi Tadilat türünde değil.");
        return claim;
    }

    private async Task<Store> LoadCoolingStoreAsync(int storeId)
        => await _db.Stores.FirstOrDefaultAsync(s => s.Id == storeId && s.Discipline == MechanicalDiscipline.Cooling)
           ?? throw new InvalidOperationException("Mağaza bulunamadı (Soğutma mağaza listesinden seçin).");

    private static string NormalizeFormNumber(string? formNumber)
    {
        var no = (formNumber ?? string.Empty).Trim();
        if (no.Length == 0) throw new InvalidOperationException("Bakım form numarası zorunludur.");
        return no;
    }

    /// <summary>Kaleme fiyat listesindeki bilgiyi uygular (miktar korunur). EUR kalem hakedişin EUR kuruyla TL'ye çevrilir.</summary>
    private static void ApplyMaintenancePricing(ServiceItem item, PriceItem price, ProgressClaim claim)
    {
        item.ItemCode = string.IsNullOrWhiteSpace(price.PozNo) ? null : price.PozNo.Trim();
        item.ItemType = string.IsNullOrWhiteSpace(price.ItemType) ? null : price.ItemType.Trim();
        item.Description = price.Description;
        item.ExportDescription = MaintenanceClaim.ExportName(price.Description, price.ItemType);
        item.Unit = string.IsNullOrWhiteSpace(price.Unit) ? "Ad" : price.Unit;
        item.LaborPrice = 0m; // Soğutma'da işçilik ayrımı yok — tek "Birim Fiyat"

        if (price.ForeignPrice is { } eur && eur > 0m && EurPricing.IsEur(price.CurrencyCode))
        {
            if (!EurPricing.IsValidRate(claim.EurRate))
                throw new InvalidOperationException("EUR fiyatlı kalem için EUR kuru girilmemiş. Hakediş Bilgileri adımından EUR kurunu girin.");
            var rate = claim.EurRate!.Value;
            item.CurrencyCode = EurPricing.CurrencyCode;
            item.ForeignPrice = eur;
            item.ExchangeRate = rate;
            item.ExchangeRateDate = claim.EurRateDate;
            item.ExchangeRateActualDate = claim.EurRateDate;
            item.ExchangeRateSource = claim.EurRateSource ?? "Manuel";
            item.MaterialPrice = EurPricing.ToTl(eur, rate);
            item.UnitPrice = item.MaterialPrice;
            item.TotalPrice = EurPricing.TotalTl(item.Quantity, eur, rate);
        }
        else
        {
            item.CurrencyCode = null;
            item.ForeignPrice = null;
            item.ExchangeRate = null;
            item.ExchangeRateDate = null;
            item.ExchangeRateActualDate = null;
            item.ExchangeRateSource = null;
            item.MaterialPrice = price.MaterialPrice + price.LaborPrice;
            item.UnitPrice = item.MaterialPrice;
            item.TotalPrice = Math.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
        }
    }

    private static void RecalculateTotal(ServiceItem item)
        => item.TotalPrice = item.ForeignPrice.HasValue && item.ExchangeRate.HasValue && EurPricing.IsEur(item.CurrencyCode)
            ? EurPricing.TotalTl(item.Quantity, item.ForeignPrice.Value, item.ExchangeRate.Value)
            : Math.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Formda mağazanın iline uygun servis bedelinin bulunmasını sağlar (yoksa en başa ekler, il türü değiştiyse değiştirir).
    /// Karar mantığı ekranla ortak: <see cref="MaintenanceClaim.ApplyServiceFee"/>.
    /// </summary>
    private async Task<MaintenanceClaim.ServiceFeeResult> EnsureMaintenanceFeeAsync(ServiceForm form, Store store)
    {
        var prices = await _db.PriceItems
            .Where(p => p.Discipline == MechanicalDiscipline.Cooling && p.IsActive
                     && (p.Description.Contains("SERVİS BEDELİ") || p.PozNo == MaintenanceClaim.InCityFeeCode || p.PozNo == MaintenanceClaim.OutOfCityFeeCode))
            .ToListAsync();
        var priceDtos = prices.Select(p => new PriceItemDto
        { Id = p.Id, PozNo = p.PozNo, Description = p.Description, Unit = p.Unit, MaterialPrice = p.MaterialPrice, LaborPrice = p.LaborPrice }).ToList();

        var entities = form.ServiceItems.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList();
        var dtos = entities.Select(i => new ServiceItemDto { Id = i.Id, Description = i.Description, ItemCode = i.ItemCode, Quantity = i.Quantity }).ToList();
        var known = dtos.ToHashSet();

        var result = MaintenanceClaim.ApplyServiceFee(dtos, store.City,
            MaintenanceClaim.FindFee(priceDtos, inCity: true), MaintenanceClaim.FindFee(priceDtos, inCity: false));

        if (result == MaintenanceClaim.ServiceFeeResult.Added)
        {
            var fee = dtos.First(d => !known.Contains(d));
            _db.ServiceItems.Add(FeeEntity(new ServiceItem
            {
                ServiceForm = form,
                ItemKey = Guid.NewGuid().ToString(),
                SortOrder = entities.Count == 0 ? 1 : entities.Min(i => i.SortOrder) - 1,   // servis bedeli formun İLK kalemi
            }, fee));
        }
        else if (result == MaintenanceClaim.ServiceFeeResult.Swapped)
        {
            var changed = dtos.First(d => MaintenanceClaim.IsServiceFee(d.ItemCode, d.Description));
            FeeEntity(entities.First(e => e.Id == changed.Id), changed);
        }
        return result;
    }

    private static ServiceItem FeeEntity(ServiceItem entity, ServiceItemDto fee)
    {
        entity.Description = fee.Description;
        entity.ExportDescription = fee.ExportDescription;
        entity.ItemCode = fee.ItemCode;
        entity.ItemType = null;
        entity.Unit = fee.Unit;
        entity.Quantity = fee.Quantity;
        entity.MaterialPrice = fee.MaterialPrice;
        entity.LaborPrice = fee.LaborPrice;
        entity.UnitPrice = fee.UnitPrice;
        entity.TotalPrice = fee.TotalPrice;
        entity.CurrencyCode = null;
        entity.ForeignPrice = null;
        entity.ExchangeRate = null;
        return entity;
    }

    /// <summary>
    /// Kalemin ekleneceği form kaydını bulur ya da oluşturur (aynı dosyada aynı mağaza + tarih + form no → o form; yoksa dosyanın
    /// boş yer tutucusu; o da yoksa aynı dosyaya bağlı yeni kayıt), mağazanın iline göre servis bedelini ekler ve kaydeder.
    /// </summary>
    private async Task<(ProgressClaim Claim, ServiceForm Target, MaintenanceClaim.ServiceFeeResult Fee)> ResolveMaintenanceTargetAsync(
        int sourceFormId, int storeId, DateTime formDate, string formNumber)
    {
        var source = await _db.ServiceForms.FirstOrDefaultAsync(f => f.Id == sourceFormId)
            ?? throw new InvalidOperationException($"Form bulunamadı: {sourceFormId}");
        var claim = await LoadMaintenanceClaimAsync(source.ProgressClaimId);
        var store = await LoadCoolingStoreAsync(storeId);
        var number = NormalizeFormNumber(formNumber);
        var date = formDate.Date;

        var siblings = await _db.ServiceForms.Include(f => f.ServiceItems)
            .Where(f => f.ProgressClaimId == source.ProgressClaimId && f.FilePath == source.FilePath && !f.IsArchived)
            .ToListAsync();
        var target = siblings.FirstOrDefault(f => f.StoreId == store.Id && f.FormDate == date
                         && string.Equals(f.FormNumber?.Trim(), number, StringComparison.OrdinalIgnoreCase))
                     // henüz kalem girilmemiş yer tutucu (yüklenen dosyanın ilk kaydı; mağazası seçilmiş ama kalemsiz kalmış kayıt da sayılır)
                     ?? siblings.FirstOrDefault(f => f.ServiceItems.Count == 0);
        if (target is null)
        {
            target = new ServiceForm
            {
                ProgressClaimId = source.ProgressClaimId,
                OriginalFileName = source.OriginalFileName,
                FilePath = source.FilePath,   // aynı dosya — kopyalanmaz
                FileSize = source.FileSize,
                FileHash = null,              // tekrar (duplicate) denetimi yalnızca yükleme sırasında yapılır
                UploadedAt = source.UploadedAt,
            };
            _db.ServiceForms.Add(target);
        }
        target.StoreId = store.Id;
        target.Store = store;
        target.FormDate = date;
        target.FormNumber = number;
        target.Status = ServiceFormStatus.Tamamlandi;

        var fee = await EnsureMaintenanceFeeAsync(target, store);
        await _db.SaveChangesAsync();   // form + servis bedeli kaydedilir (kalemlerin kimliği oluşur)
        return (claim, target, fee);
    }

    private static int NextSortOrder(ServiceForm form) => form.ServiceItems.Count == 0 ? 2 : form.ServiceItems.Max(i => i.SortOrder) + 1;

    public async Task<MaintenanceEntryResult> AddMaintenanceItemAsync(int sourceFormId, int storeId, DateTime formDate, string formNumber,
        int? priceItemId, decimal quantity)
    {
        // Girdi denetimi form kaydı oluşturulmadan ÖNCE yapılır (geçersiz istek boş form bırakmasın)
        NormalizeFormNumber(formNumber);
        PriceItem? price = null;
        if (priceItemId.HasValue)
        {
            if (quantity <= 0m) throw new InvalidOperationException("Miktar sıfırdan büyük olmalıdır.");
            price = await _db.PriceItems.FirstOrDefaultAsync(p => p.Id == priceItemId.Value && p.Discipline == MechanicalDiscipline.Cooling)
                ?? throw new InvalidOperationException("Kalem Soğutma fiyat listesinde bulunamadı.");
        }
        var owner = await _db.ServiceForms.Where(f => f.Id == sourceFormId).Select(f => (int?)f.ProgressClaimId).FirstOrDefaultAsync()
            ?? throw new InvalidOperationException($"Form bulunamadı: {sourceFormId}");
        var ownerClaim = await LoadMaintenanceClaimAsync(owner);
        if (price is { ForeignPrice: > 0m } && EurPricing.IsEur(price.CurrencyCode) && !EurPricing.IsValidRate(ownerClaim.EurRate))
            throw new InvalidOperationException("EUR fiyatlı kalem için EUR kuru girilmemiş. Hakediş Bilgileri adımından EUR kurunu girin.");

        var (claim, target, fee) = await ResolveMaintenanceTargetAsync(sourceFormId, storeId, formDate, formNumber);

        ServiceItem? item = null;
        bool merged = false;
        if (price is not null)
        {
            var code = string.IsNullOrWhiteSpace(price.PozNo) ? null : price.PozNo.Trim();
            bool priceIsFee = MaintenanceClaim.IsServiceFee(code, price.Description);

            // Aynı kalem bu formda zaten varsa miktarı artır — aynı satır iki kez oluşmasın
            item = priceIsFee
                ? target.ServiceItems.FirstOrDefault(i => MaintenanceClaim.IsServiceFee(i.ItemCode, i.Description)
                                                          && string.Equals(i.ItemCode, code, StringComparison.OrdinalIgnoreCase))
                : target.ServiceItems.FirstOrDefault(i => i.ItemCode == code && i.Description == price.Description);
            if (item is not null)
            {
                // Servis bedeli az önce otomatik eklendiyse kullanıcı aynı bedeli seçmiş demektir: toplanmaz, girilen miktar yazılır
                item.Quantity = priceIsFee && fee == MaintenanceClaim.ServiceFeeResult.Added ? quantity : item.Quantity + quantity;
                RecalculateTotal(item);
                merged = true;
            }
            else
            {
                item = new ServiceItem
                {
                    ServiceForm = target,
                    Quantity = quantity,
                    ItemKey = Guid.NewGuid().ToString(),
                    SortOrder = NextSortOrder(target),
                };
                ApplyMaintenancePricing(item, price, claim);
                _db.ServiceItems.Add(item);
            }
            await _db.SaveChangesAsync();
        }

        return new MaintenanceEntryResult((await GetByIdAsync(target.Id))!, item?.Id, merged, fee);
    }

    public async Task<MaintenanceEntryResult> AddCustomMaintenanceItemAsync(int sourceFormId, int storeId, DateTime formDate, string formNumber,
        CustomClaimItemInput input)
    {
        // Girdi denetimi form kaydı oluşturulmadan ÖNCE
        NormalizeFormNumber(formNumber);
        var name = string.Join(' ', (input.Name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new InvalidOperationException("Malzeme adı zorunludur.");
        if (input.Quantity <= 0m) throw new InvalidOperationException("Miktar sıfırdan büyük olmalıdır.");
        var (unitPrice, codeLabel) = MaintenanceClaim.CustomItemPricing(input.Basis, input.BaseAmount, input.Percent);
        var type = string.Join(' ', (input.Type ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var unit = string.IsNullOrWhiteSpace(input.Unit) ? "ADET" : input.Unit.Trim();

        var (_, target, fee) = await ResolveMaintenanceTargetAsync(sourceFormId, storeId, formDate, formNumber);

        // Fatura bazlı kalem: fatura kaydı (dosyasıyla) forma bağlanır. Tutar KALEMDE tutulur; fatura kaydının CalculatedTotal'ı 0'dır —
        // aksi halde hakediş toplamı hesaplanırken (kalemler + faturalar) aynı tutar iki kez sayılırdı.
        Invoice? invoice = null;
        if (input.Basis == CustomPriceBasis.InvoicePlusMarkup)
        {
            invoice = new Invoice
            {
                ServiceForm = target,
                FileName = string.IsNullOrWhiteSpace(input.InvoiceFileName) ? null : input.InvoiceFileName.Trim(),
                FilePath = string.IsNullOrWhiteSpace(input.InvoiceFilePath) ? null : input.InvoiceFilePath,
                Description = name.Length > 500 ? name[..500] : name,
                InvoiceAmount = input.BaseAmount,
                Quantity = input.Quantity,
                MarkupRate = input.Percent / 100m,
                CalculatedTotal = 0m,
                UploadedAt = DateTime.Now,
            };
            _db.Invoices.Add(invoice);
        }

        var item = new ServiceItem
        {
            ServiceForm = target,
            Description = type.Length > 0 ? $"{name} {type}" : name,
            ExportDescription = name,
            ItemType = type.Length > 0 ? type : null,
            // çıktıda MALZEME KODU: kullanıcı kod girdiyse o ("ELK.MLZ. 3.6"), yoksa fiyat türünün etiketi ("FATURA +%10 M.K." ...)
            ItemCode = string.IsNullOrWhiteSpace(input.Code) ? codeLabel : input.Code.Trim(),
            Quantity = input.Quantity,
            Unit = unit,
            MaterialPrice = unitPrice,
            LaborPrice = 0m,
            UnitPrice = unitPrice,
            TotalPrice = Math.Round(unitPrice * input.Quantity, 2, MidpointRounding.AwayFromZero),
            IsManualEntry = true,                 // fiyat listesinden gelmedi; genel fiyat listesine de EKLENMEZ
            IsInvoiceBased = invoice is not null,
            Invoice = invoice,
            ItemKey = Guid.NewGuid().ToString(),
            SortOrder = NextSortOrder(target),
        };
        _db.ServiceItems.Add(item);
        await _db.SaveChangesAsync();

        return new MaintenanceEntryResult((await GetByIdAsync(target.Id))!, item.Id, false, fee);
    }

    public async Task<ServiceFormDto> UpdateMaintenanceEntryAsync(int formId, int storeId, DateTime formDate, string formNumber, int? itemId, decimal? quantity)
    {
        var form = await _db.ServiceForms.Include(f => f.ServiceItems).FirstOrDefaultAsync(f => f.Id == formId)
            ?? throw new InvalidOperationException($"Form bulunamadı: {formId}");
        await LoadMaintenanceClaimAsync(form.ProgressClaimId);
        var store = await LoadCoolingStoreAsync(storeId);
        var number = NormalizeFormNumber(formNumber);
        var date = formDate.Date;

        var clash = await _db.ServiceForms.AnyAsync(f => f.Id != formId && f.ProgressClaimId == form.ProgressClaimId && !f.IsArchived
            && f.FilePath == form.FilePath && f.StoreId == store.Id && f.FormDate == date && f.FormNumber == number);
        if (clash)
            throw new InvalidOperationException("Bu mağaza, tarih ve bakım form numarasıyla başka bir kayıt zaten var; kalemi o forma ekleyin.");

        form.StoreId = store.Id;
        form.Store = store;
        form.FormDate = date;
        form.FormNumber = number;
        form.Status = ServiceFormStatus.Tamamlandi;
        // İl türü değiştiyse şehir içi ↔ dışı bedeli değişir; bedeli silinmiş forma yeniden EKLENMEZ (kullanıcı bilerek silmiş olabilir)
        if (form.ServiceItems.Any(i => MaintenanceClaim.IsServiceFee(i.ItemCode, i.Description)))
            await EnsureMaintenanceFeeAsync(form, store);

        if (itemId.HasValue && quantity.HasValue)
        {
            if (quantity.Value <= 0m) throw new InvalidOperationException("Miktar sıfırdan büyük olmalıdır.");
            var item = form.ServiceItems.FirstOrDefault(i => i.Id == itemId.Value)
                ?? throw new InvalidOperationException("Kalem bu formda bulunamadı.");
            item.Quantity = quantity.Value;
            RecalculateTotal(item);
        }
        // bedel değiştiyse (birim fiyat farklı) toplamı yeniden hesapla
        foreach (var feeItem in form.ServiceItems.Where(i => MaintenanceClaim.IsServiceFee(i.ItemCode, i.Description)))
            RecalculateTotal(feeItem);

        await _db.SaveChangesAsync();
        return (await GetByIdAsync(formId))!;
    }

    /// <summary>Kaleme bağlı fatura kaydını kaldırır; dosyası varsa arşive taşınır (başka kalem kullanmıyorsa).</summary>
    private async Task RemoveAttachedInvoiceAsync(int invoiceId)
    {
        if (await _db.ServiceItems.AnyAsync(i => i.InvoiceId == invoiceId)) return;
        if (_cleanup is not null)
        {
            await _cleanup.ArchiveRemovedInvoiceAsync(invoiceId);   // dosyayı arşive taşır + kaydı siler
            return;
        }
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice is null) return;
        _db.Invoices.Remove(invoice);
        await _db.SaveChangesAsync();
    }

    public async Task<ServiceFormDto?> RemoveMaintenanceItemAsync(int itemId)
    {
        var item = await _db.ServiceItems.FirstOrDefaultAsync(i => i.Id == itemId)
            ?? throw new InvalidOperationException($"Kalem bulunamadı: {itemId}");
        var form = await _db.ServiceForms.Include(f => f.ServiceItems).FirstAsync(f => f.Id == item.ServiceFormId);
        await LoadMaintenanceClaimAsync(form.ProgressClaimId);

        var invoiceId = item.InvoiceId;
        _db.ServiceItems.Remove(item);
        await _db.SaveChangesAsync();
        if (invoiceId.HasValue) await RemoveAttachedInvoiceAsync(invoiceId.Value);

        if (form.ServiceItems.Any(i => i.Id != itemId))
        {
            await _db.SaveChangesAsync();
            return (await GetByIdAsync(form.Id))!;
        }

        // Formda kalem kalmadı: form kaydı da kalkar
        var hasOtherEntries = await _db.ServiceForms.AnyAsync(f => f.Id != form.Id
            && f.ProgressClaimId == form.ProgressClaimId && f.FilePath == form.FilePath && !f.IsArchived);
        if (hasOtherEntries)
        {
            _db.ServiceForms.Remove(form);   // yalnızca bu kayıt; dosya ve diğer kayıtlar dokunulmaz
            await _db.SaveChangesAsync();
            return null;
        }
        // dosyanın son kaydı: dosya hakedişte kalır, kayıt boş yer tutucuya döner
        form.StoreId = null;
        form.FormDate = null;
        form.FormNumber = null;
        form.Status = ServiceFormStatus.Bekliyor;
        await _db.SaveChangesAsync();
        return (await GetByIdAsync(form.Id))!;
    }

    public async Task<ServiceFormDto> ClearGlycolRowAsync(int formId)
    {
        var form = await _db.ServiceForms.Include(f => f.ServiceItems).FirstOrDefaultAsync(f => f.Id == formId)
            ?? throw new InvalidOperationException($"Form bulunamadı: {formId}");
        var (claim, price) = await LoadGlycolContextAsync(form.ProgressClaimId);
        var code = price.PozNo ?? GlycolClaim.FallbackItemCode;

        form.StoreId = null;
        form.FormDate = null;
        form.FormNumber = null;
        form.Status = ServiceFormStatus.Bekliyor;

        var glycol = form.ServiceItems.FirstOrDefault(i => i.ItemCode == code);
        if (glycol is not null)
        {
            glycol.Quantity = 0m;
            ApplyGlycolPricing(glycol, price, claim);
        }
        await _db.SaveChangesAsync();
        return (await GetByIdAsync(formId))!;
    }

    public async Task DeleteAsync(int id)
    {
        var form = await _db.ServiceForms.FindAsync(id);
        if (form is null) return;

        // Aynı dosyaya bağlı diğer kayıtlar (Soğutma Glikol: bir dosyadan çok kayıt) birlikte kaldırılır
        if (!string.IsNullOrEmpty(form.FilePath))
        {
            var siblings = await _db.ServiceForms.Include(f => f.ServiceItems)
                .Where(f => f.Id != id && f.ProgressClaimId == form.ProgressClaimId && f.FilePath == form.FilePath && !f.IsArchived)
                .ToListAsync();
            if (siblings.Count > 0)
            {
                _db.ServiceItems.RemoveRange(siblings.SelectMany(s => s.ServiceItems));
                _db.ServiceForms.RemoveRange(siblings);
                await _db.SaveChangesAsync();
            }
        }
        await _cleanup.ArchiveRemovedServiceFormAsync(id);
    }

    public async Task<List<ServiceFormDto>> GetIncompleteFormsAsync(int claimId)
    {
        var forms = await _db.ServiceForms
            .Include(f => f.Store)
            .Include(f => f.ServiceItems)
            .Where(f => f.ProgressClaimId == claimId &&
                        (f.Status == ServiceFormStatus.Bekliyor || f.Status == ServiceFormStatus.EksikBilgi))
            .ToListAsync();
        return forms.Select(MapToDto).ToList();
    }

    private ServiceFormDto MapToDto(ServiceForm f)
    {
        var absPath = _fileStorage.GetAbsolutePath(f.FilePath);
        long sizeBytes = 0;
        try { if (File.Exists(absPath)) sizeBytes = new FileInfo(absPath).Length; } catch { }

        return new ServiceFormDto
        {
            Id = f.Id,
            ProgressClaimId = f.ProgressClaimId,
            StoreId = f.StoreId,
            StoreCode = f.Store?.Code ?? string.Empty,
            StoreName = f.Store?.Name ?? string.Empty,
            StoreFormat = f.Store?.Format,
            StoreRegion = f.Store?.Region,
            StoreCity = f.Store?.City,
            FormNumber = f.FormNumber,
            FormDate = f.FormDate,
            OriginalFileName = f.OriginalFileName,
            FilePath = f.FilePath,
            FileUrl = _fileStorage.GetFileUrl(f.FilePath),
            FileSizeBytes = sizeBytes,
            UploadedAt = f.UploadedAt,
            Status = f.Status,
            Notes = f.Notes,
            ServiceItems = f.ServiceItems.OrderBy(i => i.SortOrder).Select(i => new ServiceItemDto
            {
                Id = i.Id, ServiceFormId = i.ServiceFormId, Description = i.Description,
                Quantity = i.Quantity, Unit = i.Unit, MaterialPrice = i.MaterialPrice,
                LaborPrice = i.LaborPrice, UnitPrice = i.UnitPrice, TotalPrice = i.TotalPrice,
                IsInvoiceBased = i.IsInvoiceBased, InvoiceId = i.InvoiceId, SortOrder = i.SortOrder,
                IsManualEntry = i.IsManualEntry,
                IsAutoGenerated = i.IsAutoGenerated,
                AutoGeneratedType = i.AutoGeneratedType,
                AutoRuleTrigger = i.AutoRuleTrigger,
                ExportDescription = i.ExportDescription,
                LocalId = string.IsNullOrEmpty(i.ItemKey) ? Guid.NewGuid() : Guid.Parse(i.ItemKey),
                ParentLocalId = string.IsNullOrEmpty(i.ParentItemKey) ? (Guid?)null : Guid.Parse(i.ParentItemKey),
                IsCurrencyBased = i.IsCurrencyBased,
                CurrencyCode = i.CurrencyCode,
                ListPriceUsd = i.ListPriceUsd,
                DiscountRate = i.DiscountRate,
                DiscountedUsdPrice = i.DiscountedUsdPrice,
                ExchangeRate = i.ExchangeRate,
                ExchangeRateDate = i.ExchangeRateDate,
                ExchangeRateActualDate = i.ExchangeRateActualDate,
                ExchangeRateSource = i.ExchangeRateSource,
                ForeignPrice = i.ForeignPrice,
                ItemCode = i.ItemCode,
                ItemType = i.ItemType
            }).ToList(),
            Invoices = f.Invoices.Select(inv => new InvoiceDto
            {
                Id = inv.Id, ServiceFormId = inv.ServiceFormId, FileName = inv.FileName,
                FilePath = inv.FilePath, FileUrl = inv.FilePath is not null ? _fileStorage.GetFileUrl(inv.FilePath) : null,
                Description = inv.Description, InvoiceAmount = inv.InvoiceAmount,
                Quantity = inv.Quantity, MarkupRate = inv.MarkupRate,
                CalculatedTotal = inv.CalculatedTotal, UploadedAt = inv.UploadedAt
            }).ToList()
        };
    }
}
