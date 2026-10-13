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
            .Where(f => f.ProgressClaimId == claimId)
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
        var defaultFee = glycolClaim ? 0m : await _settings.GetDecimalAsync("DefaultServiceFee", 4550m);

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

        // Otomatik Servis Ücreti satırı ekle (Glikol hakedişi hariç)
        if (!glycolClaim)
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
        _db.ServiceItems.Add(defaultItem);
        await _db.SaveChangesAsync();

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
                ItemCode = i.ItemCode
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
