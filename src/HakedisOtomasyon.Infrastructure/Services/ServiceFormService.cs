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
        var defaultFee = await _settings.GetDecimalAsync("DefaultServiceFee", 4550m);

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

        // Otomatik Servis Ücreti satırı ekle
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

        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Code == storeCode.Trim().ToUpper())
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
                ExchangeRateSource = i.ExchangeRateSource
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
