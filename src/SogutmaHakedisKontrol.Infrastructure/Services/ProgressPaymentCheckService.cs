using ClosedXML.Excel;
using SogutmaHakedisKontrol.Application.DTOs;
using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Entities;
using SogutmaHakedisKontrol.Domain.Enums;
using SogutmaHakedisKontrol.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

public class ProgressPaymentCheckService : IProgressPaymentCheckService
{
    private const decimal AutoMatchThreshold = 0.995m;
    private const decimal FuzzyMinThreshold = 0.60m;
    private const decimal ToleranceTry = 1.0m;      // ±1 TL yuvarlama toleransı
    private const decimal TolerancePercent = 0.5m;   // ±%0.5 yuvarlama toleransı

    private static readonly XLColor CorrectionFillColor = XLColor.FromArgb(255, 199, 206);
    private static readonly XLColor CorrectionFontColor = XLColor.FromArgb(156, 0, 6);

    private readonly AppDbContext _db;
    private readonly IMaterialMatchingService _matching;
    private readonly IAppPathService _appPath;
    private readonly IUnitPriceListService _unitPriceList;

    public ProgressPaymentCheckService(AppDbContext db, IMaterialMatchingService matching, IAppPathService appPath, IUnitPriceListService unitPriceList)
    {
        _db = db;
        _matching = matching;
        _appPath = appPath;
        _unitPriceList = unitPriceList;
    }

    // ------------------------------------------------------------------ //
    //  OKUMA
    // ------------------------------------------------------------------ //
    public async Task<List<ProgressPaymentCheckDto>> GetHistoryAsync()
    {
        var checks = await _db.ProgressPaymentChecks.OrderByDescending(c => c.CreatedAt).ToListAsync();
        var result = new List<ProgressPaymentCheckDto>();
        foreach (var c in checks)
        {
            var items = await _db.ProgressPaymentCheckItems.Where(i => i.ProgressPaymentCheckId == c.Id).ToListAsync();
            result.Add(MapCheck(c, items));
        }
        return result;
    }

    public async Task<ProgressPaymentCheckDto?> GetByIdAsync(int id)
    {
        var c = await _db.ProgressPaymentChecks.FindAsync(id);
        if (c is null) return null;
        var items = await _db.ProgressPaymentCheckItems.Where(i => i.ProgressPaymentCheckId == id).ToListAsync();
        return MapCheck(c, items);
    }

    public async Task<List<ProgressPaymentCheckItemDto>> GetItemsAsync(int checkId)
    {
        var items = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId)
            .OrderBy(i => i.StoreName).ThenBy(i => i.SourceRowNumber)
            .ToListAsync();
        return items.Select(MapItem).ToList();
    }

    public Task<ProgressPaymentImportPreviewDto> ParseExcelAsync(Stream stream, string fileName, int unitPriceListId)
        => Task.FromResult(ProgressPaymentExcelParser.Parse(stream, fileName));

    // ------------------------------------------------------------------ //
    //  OLUŞTURMA + OTOMATİK EŞLEŞTİRME
    // ------------------------------------------------------------------ //
    public async Task<ProgressPaymentCheckDto> CreateCheckAsync(
        int unitPriceListId, string companyName, string region, string claimTypeName,
        int year, int month, string periodLabel,
        string originalFileName, byte[] originalFileBytes,
        decimal? exchangeRateEur,
        ProgressPaymentImportPreviewDto parsed)
    {
        var originalPath = SaveOriginalFile(companyName, year, month, originalFileName, originalFileBytes);

        var check = new ProgressPaymentCheck
        {
            UnitPriceListId = unitPriceListId,
            CompanyName = companyName,
            Region = region,
            ClaimTypeName = claimTypeName,
            Year = year,
            Month = month,
            PeriodLabel = periodLabel,
            OriginalFileName = originalFileName,
            OriginalFilePath = originalPath,
            ExchangeRateEur = exchangeRateEur,
            ExchangeRateEnteredAt = exchangeRateEur.HasValue ? DateTime.Now : null,
            // Firma Toplamı = itemize edilmiş satırların toplamı (satır bazlı Fark hesabıyla tutarlı olan tek rakam budur).
            // GENEL ICMAL özet sayfasındaki toplam bazı hakediş türlerinde itemize edilmemiş bir taban bedel içerebilir
            // (gerçek veriyle doğrulandı) — bu yüzden satır bazlı kontrolün referansı olarak kullanılmaz.
            CompanyTotal = parsed.Items.Count > 0 ? parsed.Items.Sum(i => i.CompanyLineTotal) : (parsed.DetectedCompanyGrandTotal ?? 0),
            Status = ProgressPaymentCheckStatus.Taslak,
            CreatedAt = DateTime.Now,
        };
        _db.ProgressPaymentChecks.Add(check);
        await _db.SaveChangesAsync();

        foreach (var dto in parsed.Items)
        {
            _db.ProgressPaymentCheckItems.Add(new ProgressPaymentCheckItem
            {
                ProgressPaymentCheckId = check.Id,
                SheetName = dto.SheetName,
                SourceRowNumber = dto.SourceRowNumber,
                MaterialCellRef = dto.MaterialCellRef,
                QuantityCellRef = dto.QuantityCellRef,
                UnitPriceCellRef = dto.UnitPriceCellRef,
                LineTotalCellRef = dto.LineTotalCellRef,
                StoreCode = dto.StoreCode,
                StoreName = dto.StoreName,
                StoreFormat = dto.StoreFormat,
                VisitDate = dto.VisitDate,
                MaintenanceFormNo = dto.MaintenanceFormNo,
                OriginalItemCode = dto.OriginalItemCode,
                OriginalMaterialName = dto.OriginalMaterialName,
                OriginalMaterialSpec = dto.OriginalMaterialSpec,
                IsServiceItem = dto.IsServiceItem,
                Quantity = dto.Quantity,
                Unit = dto.Unit,
                CompanyUnitPrice = dto.CompanyUnitPrice,
                CompanyLineTotal = dto.CompanyLineTotal,
                MatchStatus = MaterialMatchStatus.Unmatched,
                ControlStatus = CheckItemControlStatus.KontrolGerekli,
                CreatedAt = DateTime.Now,
            });
        }
        await _db.SaveChangesAsync();

        await AutoMatchAsync(check.Id, unitPriceListId, companyName);
        await RecalculateAsync(check.Id);

        var updated = await _db.ProgressPaymentChecks.FindAsync(check.Id);
        var items = await _db.ProgressPaymentCheckItems.Where(i => i.ProgressPaymentCheckId == check.Id).ToListAsync();
        return MapCheck(updated!, items);
    }

    /// <summary>
    /// Aynı orijinal ad+tip'e sahip satırları tek grup olarak değerlendirir (aynı soru tekrar sorulmaz).
    /// Alias/exact eşleşenleri otomatik onaylar; geri kalanları turuncu kuyruğa (OnayBekliyor) bırakır.
    /// </summary>
    private async Task AutoMatchAsync(int checkId, int unitPriceListId, string companyName)
    {
        var items = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId && !i.IsExcluded)
            .ToListAsync();

        var groups = items.GroupBy(i => (Name: i.OriginalMaterialName.Trim(), Spec: (i.OriginalMaterialSpec ?? "").Trim()));

        foreach (var group in groups)
        {
            var candidates = await _matching.FindCandidatesAsync(
                unitPriceListId, group.Key.Name, group.Key.Spec, companyName, maxResults: 5);

            var best = candidates.FirstOrDefault();
            foreach (var item in group)
            {
                if (best is null)
                {
                    item.MatchStatus = MaterialMatchStatus.Unmatched;
                    continue;
                }

                if (best.Confidence >= AutoMatchThreshold && !best.SpecMismatchWarning)
                {
                    item.MatchedUnitPriceItemId = best.UnitPriceItemId;
                    item.MatchedMaterialName = string.IsNullOrWhiteSpace(best.Spec) ? best.MaterialName : $"{best.MaterialName} — {best.Spec}";
                    item.MatchConfidence = best.Confidence;
                    item.MatchStatus = best.Confidence >= 1.0m ? MaterialMatchStatus.Exact : MaterialMatchStatus.LearnedAlias;
                }
                else if (best.Confidence >= FuzzyMinThreshold)
                {
                    item.MatchedUnitPriceItemId = best.UnitPriceItemId;
                    item.MatchedMaterialName = string.IsNullOrWhiteSpace(best.Spec) ? best.MaterialName : $"{best.MaterialName} — {best.Spec}";
                    item.MatchConfidence = best.Confidence;
                    item.MatchStatus = MaterialMatchStatus.FuzzyPending;
                }
                else
                {
                    item.MatchStatus = MaterialMatchStatus.Unmatched;
                }
            }
        }
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ //
    //  TURUNCU KUYRUK
    // ------------------------------------------------------------------ //
    public async Task<List<MaterialMatchQueueEntryDto>> GetPendingMatchQueueAsync(int checkId)
    {
        var check = await _db.ProgressPaymentChecks.FindAsync(checkId)
            ?? throw new InvalidOperationException("Kontrol kaydı bulunamadı.");

        var pending = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId && i.MatchStatus == MaterialMatchStatus.FuzzyPending && !i.IsExcluded)
            .ToListAsync();

        var result = new List<MaterialMatchQueueEntryDto>();
        var groups = pending.GroupBy(i => (Name: i.OriginalMaterialName.Trim(), Spec: (i.OriginalMaterialSpec ?? "").Trim()));

        foreach (var group in groups)
        {
            var candidates = await _matching.FindCandidatesAsync(
                check.UnitPriceListId, group.Key.Name, group.Key.Spec, check.CompanyName, maxResults: 5);

            result.Add(new MaterialMatchQueueEntryDto
            {
                CheckItemIds = group.Select(i => i.Id).ToList(),
                OriginalMaterialName = group.Key.Name,
                OriginalMaterialSpec = group.Key.Spec,
                OccurrenceCount = group.Count(),
                Candidates = candidates,
            });
        }
        return result.OrderByDescending(r => r.OccurrenceCount).ToList();
    }

    public async Task ResolveMatchAsync(int checkId, List<int> checkItemIds, int unitPriceItemId, bool saveAsAlias, string? companyName)
    {
        var unitPriceItem = await _db.UnitPriceItems.FindAsync(unitPriceItemId)
            ?? throw new InvalidOperationException("Birim fiyat kalemi bulunamadı.");

        var items = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId && checkItemIds.Contains(i.Id))
            .ToListAsync();

        string? firstOriginalName = items.FirstOrDefault()?.OriginalMaterialName;

        foreach (var item in items)
        {
            var oldMatch = item.MatchedMaterialName;
            item.MatchedUnitPriceItemId = unitPriceItem.Id;
            item.MatchedMaterialName = string.IsNullOrWhiteSpace(unitPriceItem.Spec)
                ? unitPriceItem.MaterialName : $"{unitPriceItem.MaterialName} — {unitPriceItem.Spec}";
            item.MatchConfidence = 1.0m;
            item.MatchStatus = MaterialMatchStatus.ManuallyMatched;
            LogAction(item, "Eslestir", oldMatch, item.MatchedMaterialName, $"\"{item.OriginalMaterialName}\" kalemi \"{item.MatchedMaterialName}\" ile eşleştirildi.");
        }
        await _db.SaveChangesAsync();

        if (saveAsAlias && !string.IsNullOrWhiteSpace(firstOriginalName))
        {
            var spec = items.FirstOrDefault()?.OriginalMaterialSpec;
            var aliasText = string.IsNullOrWhiteSpace(spec) ? firstOriginalName : $"{firstOriginalName} {spec}";
            await _matching.SaveAliasAsync(companyName, aliasText!, unitPriceItem.Id);
        }

        await UpdateCheckStatusAsync(checkId);
        await RecalculateAsync(checkId);
    }

    public async Task RejectMatchAsync(int checkId, List<int> checkItemIds)
    {
        var items = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId && checkItemIds.Contains(i.Id))
            .ToListAsync();
        foreach (var item in items)
        {
            item.MatchedUnitPriceItemId = null;
            item.MatchedMaterialName = null;
            item.MatchConfidence = null;
            item.MatchStatus = MaterialMatchStatus.Unmatched;
        }
        await _db.SaveChangesAsync();
        await UpdateCheckStatusAsync(checkId);
        await RecalculateAsync(checkId);
    }

    public async Task ExcludeItemAsync(int checkItemId, bool excluded)
    {
        var item = await _db.ProgressPaymentCheckItems.FindAsync(checkItemId);
        if (item is null) return;
        item.IsExcluded = excluded;
        await _db.SaveChangesAsync();
        await RecalculateAsync(item.ProgressPaymentCheckId);
    }

    public async Task CorrectQuantityAsync(int checkItemId, decimal newQuantity)
    {
        var item = await _db.ProgressPaymentCheckItems.FindAsync(checkItemId);
        if (item is null) return;
        var old = item.Quantity;
        item.Quantity = newQuantity;
        item.QuantityManuallyCorrected = true;
        var logNote = $"[Miktar elle düzeltildi: {old:0.####} → {newQuantity:0.####}, {DateTime.Now:dd.MM.yyyy HH:mm}]";
        item.ControlNote = string.IsNullOrWhiteSpace(item.ControlNote) ? logNote : $"{logNote} {item.ControlNote}";
        await _db.SaveChangesAsync();
        await RecalculateAsync(item.ProgressPaymentCheckId);
    }

    // ------------------------------------------------------------------ //
    //  YENİ KALEM EKLE / BU FİYAT DOĞRUDUR
    // ------------------------------------------------------------------ //
    public async Task<List<MaterialMatchCandidateDto>> FindSimilarCandidatesAsync(int checkItemId)
    {
        var item = await _db.ProgressPaymentCheckItems.FindAsync(checkItemId);
        if (item is null) return new();
        var check = await _db.ProgressPaymentChecks.FindAsync(item.ProgressPaymentCheckId);
        if (check is null) return new();
        return await _matching.FindCandidatesAsync(check.UnitPriceListId, item.OriginalMaterialName, item.OriginalMaterialSpec, check.CompanyName, maxResults: 3);
    }

    public async Task<UnitPriceItemDto> CreateAndMatchNewItemAsync(int checkId, List<int> checkItemIds, UnitPriceItemDto newItem, string? companyName, string actionLabel)
    {
        var check = await _db.ProgressPaymentChecks.FindAsync(checkId)
            ?? throw new InvalidOperationException("Kontrol kaydı bulunamadı.");

        var created = await _unitPriceList.CreateItemAsync(check.UnitPriceListId, newItem);

        var items = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId && checkItemIds.Contains(i.Id))
            .ToListAsync();

        var matchedLabel = string.IsNullOrWhiteSpace(created.Spec) ? created.MaterialName : $"{created.MaterialName} — {created.Spec}";
        var note = actionLabel == "BuFiyatDogru"
            ? $"Firma fiyatı ({created.Price:0.##} {created.Currency}) doğru kabul edilerek yeni katalog kalemi olarak eklendi."
            : "Kullanıcı tarafından yeni katalog kalemi olarak eklendi ve eşleştirildi.";

        string? firstOriginalName = items.FirstOrDefault()?.OriginalMaterialName;
        string? firstOriginalSpec = items.FirstOrDefault()?.OriginalMaterialSpec;

        foreach (var item in items)
        {
            item.MatchedUnitPriceItemId = created.Id;
            item.MatchedMaterialName = matchedLabel;
            item.MatchConfidence = 1.0m;
            item.MatchStatus = MaterialMatchStatus.ManuallyMatched;
            LogAction(item, actionLabel, item.OriginalMaterialName, matchedLabel, note);
        }
        await _db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(firstOriginalName))
        {
            var aliasText = string.IsNullOrWhiteSpace(firstOriginalSpec) ? firstOriginalName : $"{firstOriginalName} {firstOriginalSpec}";
            await _matching.SaveAliasAsync(companyName, aliasText!, created.Id);
        }

        await UpdateCheckStatusAsync(checkId);
        await RecalculateAsync(checkId);
        return created;
    }

    // ------------------------------------------------------------------ //
    //  BİRİM FİYAT DÜZELTME (Düzelt / Geri Al / Toplu Düzelt)
    // ------------------------------------------------------------------ //
    public async Task SetPriceCorrectionAsync(int checkItemId, bool apply)
    {
        var item = await _db.ProgressPaymentCheckItems.FindAsync(checkItemId);
        if (item is null) return;
        item.PriceCorrectionApplied = apply;
        LogAction(item, apply ? "Duzelt" : "GeriAl",
            item.CompanyUnitPrice.ToString("0.####"), item.ApprovedUnitPriceTry?.ToString("0.####"),
            apply
                ? $"Birim fiyat {item.CompanyUnitPrice:N2} TL yerine onaylı {item.ApprovedUnitPriceTry:N2} TL olarak düzeltildi (export'ta uygulanacak)."
                : "Fiyat düzeltmesi geri alındı.");
        await _db.SaveChangesAsync();
    }

    private static bool IsDefiniteMatch(MaterialMatchStatus s) =>
        s is MaterialMatchStatus.Exact or MaterialMatchStatus.LearnedAlias or MaterialMatchStatus.ManuallyMatched;

    public async Task<int> GetBulkPriceCorrectionPreviewCountAsync(int checkId)
    {
        var candidates = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId && !i.IsExcluded && !i.PriceCorrectionApplied
                        && i.ControlStatus == CheckItemControlStatus.FiyatHatasi)
            .ToListAsync();
        return candidates.Count(i => IsDefiniteMatch(i.MatchStatus));
    }

    public async Task<int> ApplyBulkPriceCorrectionAsync(int checkId)
    {
        var candidates = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId && !i.IsExcluded && !i.PriceCorrectionApplied
                        && i.ControlStatus == CheckItemControlStatus.FiyatHatasi)
            .ToListAsync();
        var items = candidates.Where(i => IsDefiniteMatch(i.MatchStatus)).ToList();

        foreach (var item in items)
        {
            item.PriceCorrectionApplied = true;
            LogAction(item, "TopluDuzelt", item.CompanyUnitPrice.ToString("0.####"), item.ApprovedUnitPriceTry?.ToString("0.####"),
                $"Toplu düzeltme: {item.CompanyUnitPrice:N2} TL yerine onaylı {item.ApprovedUnitPriceTry:N2} TL olarak düzeltildi.");
        }
        await _db.SaveChangesAsync();
        return items.Count;
    }

    public async Task<List<CheckItemActionLogDto>> GetActionLogAsync(int checkId)
    {
        return await _db.CheckItemActionLogs
            .Where(l => l.ProgressPaymentCheckId == checkId)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new CheckItemActionLogDto
            {
                Id = l.Id,
                ProgressPaymentCheckItemId = l.ProgressPaymentCheckItemId,
                Action = l.Action,
                OldValue = l.OldValue,
                NewValue = l.NewValue,
                Note = l.Note,
                CreatedAt = l.CreatedAt,
            }).ToListAsync();
    }

    private void LogAction(ProgressPaymentCheckItem item, string action, string? oldValue, string? newValue, string note)
    {
        _db.CheckItemActionLogs.Add(new CheckItemActionLog
        {
            ProgressPaymentCheckItemId = item.Id,
            ProgressPaymentCheckId = item.ProgressPaymentCheckId,
            Action = action,
            OldValue = oldValue,
            NewValue = newValue,
            Note = note,
            CreatedAt = DateTime.Now,
        });
    }

    private async Task UpdateCheckStatusAsync(int checkId)
    {
        var check = await _db.ProgressPaymentChecks.FindAsync(checkId);
        if (check is null || check.Status == ProgressPaymentCheckStatus.Tamamlandi) return;
        var hasPending = await _db.ProgressPaymentCheckItems
            .AnyAsync(i => i.ProgressPaymentCheckId == checkId && i.MatchStatus == MaterialMatchStatus.FuzzyPending && !i.IsExcluded);
        check.Status = hasPending ? ProgressPaymentCheckStatus.EslesmeBekliyor : ProgressPaymentCheckStatus.Taslak;
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ //
    //  FİYAT HESAPLAMA
    // ------------------------------------------------------------------ //
    public async Task RecalculateAsync(int checkId)
    {
        var check = await _db.ProgressPaymentChecks.FindAsync(checkId)
            ?? throw new InvalidOperationException("Kontrol kaydı bulunamadı.");
        var items = await _db.ProgressPaymentCheckItems.Where(i => i.ProgressPaymentCheckId == checkId).ToListAsync();

        var priceItemIds = items.Where(i => i.MatchedUnitPriceItemId.HasValue).Select(i => i.MatchedUnitPriceItemId!.Value).Distinct().ToList();
        var priceItems = await _db.UnitPriceItems.Where(p => priceItemIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        decimal calculatedTotal = 0;

        foreach (var item in items)
        {
            if (item.IsExcluded)
            {
                item.ControlStatus = CheckItemControlStatus.KontrolDisi;
                continue;
            }

            switch (item.MatchStatus)
            {
                case MaterialMatchStatus.Unmatched:
                    item.ControlStatus = CheckItemControlStatus.BirimFiyatBulunamadi;
                    item.ControlNote = "Onaylı birim fiyat listesinde karşılığı bulunamadı.";
                    item.ApprovedUnitPrice = null;
                    item.ApprovedUnitPriceTry = null;
                    item.CalculatedLineTotal = null;
                    item.Difference = null;
                    item.DifferencePercent = null;
                    continue;

                case MaterialMatchStatus.FuzzyPending:
                    item.ControlStatus = CheckItemControlStatus.OnayBekliyor;
                    item.ControlNote = "Tahmini eşleşme kullanıcı onayı bekliyor.";
                    continue;
            }

            if (!item.MatchedUnitPriceItemId.HasValue || !priceItems.TryGetValue(item.MatchedUnitPriceItemId.Value, out var priceItem))
            {
                item.ControlStatus = CheckItemControlStatus.BirimFiyatBulunamadi;
                continue;
            }

            item.ApprovedUnitPrice = priceItem.Price;
            item.ApprovedCurrency = priceItem.Currency;

            if (item.Quantity <= 0)
            {
                item.ControlStatus = CheckItemControlStatus.KontrolGerekli;
                item.ControlNote = "Miktar okunamadı veya sıfır — lütfen düzeltin.";
                item.ApprovedUnitPriceTry = null;
                item.CalculatedLineTotal = null;
                continue;
            }

            if (priceItem.Currency == "EUR")
            {
                if (!check.ExchangeRateEur.HasValue)
                {
                    item.ControlStatus = CheckItemControlStatus.KontrolGerekli;
                    item.ControlNote = "Onaylı fiyat EUR bazlı — ortalama EUR/TL kuru girilmeden hesaplanamaz.";
                    item.ApprovedUnitPriceTry = null;
                    item.CalculatedLineTotal = null;
                    continue;
                }
                item.ApprovedUnitPriceTry = Math.Round(priceItem.Price * check.ExchangeRateEur.Value, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                item.ApprovedUnitPriceTry = priceItem.Price;
            }

            var unitA = NormalizeUnit(item.Unit);
            var unitB = NormalizeUnit(priceItem.Unit);
            item.UnitMismatch = !string.IsNullOrEmpty(unitA) && !string.IsNullOrEmpty(unitB) && unitA != unitB;

            item.CalculatedLineTotal = Math.Round(item.Quantity * item.ApprovedUnitPriceTry.Value, 2, MidpointRounding.AwayFromZero);
            item.Difference = item.CompanyLineTotal - item.CalculatedLineTotal.Value;
            item.DifferencePercent = item.CalculatedLineTotal.Value != 0
                ? Math.Round(item.Difference.Value / item.CalculatedLineTotal.Value * 100m, 2, MidpointRounding.AwayFromZero)
                : (decimal?)null;

            calculatedTotal += item.CalculatedLineTotal.Value;

            if (item.UnitMismatch)
            {
                item.ControlStatus = CheckItemControlStatus.BirimUyusmazligi;
                item.ControlNote = $"Hakedişte birim \"{item.Unit}\", onaylı listede \"{priceItem.Unit}\" — birimler uyuşmuyor, tutar hesaplanmadı.";
            }
            else if (Math.Abs(item.Difference.Value) <= ToleranceTry ||
                     (item.DifferencePercent.HasValue && Math.Abs(item.DifferencePercent.Value) <= TolerancePercent))
            {
                item.ControlStatus = CheckItemControlStatus.Uygun;
                item.ControlNote = "Birim fiyat listesine uygundur.";
            }
            else
            {
                item.ControlStatus = CheckItemControlStatus.FiyatHatasi;
                var yon = item.Difference.Value > 0 ? "fazla" : "eksik";
                item.ControlNote = $"Firma tutarı: {item.CompanyLineTotal:N2} TL / Hesaplanan: {item.CalculatedLineTotal:N2} TL. " +
                    $"{Math.Abs(item.Difference.Value):N2} TL ({Math.Abs(item.DifferencePercent ?? 0):N1}%) {yon} yazılmış.";
            }
        }

        check.CalculatedTotal = calculatedTotal;
        check.Difference = check.CompanyTotal - calculatedTotal;
        await _db.SaveChangesAsync();
    }

    private static string NormalizeUnit(string? u)
    {
        if (string.IsNullOrWhiteSpace(u)) return string.Empty;
        var t = u.Trim().ToLowerInvariant().Replace(".", "");
        return t switch
        {
            "metre" or "mt" or "m" => "m",
            "adet" or "ad" or "adt" or "ad" => "adet",
            "kilogram" or "kg" => "kg",
            "litre" or "lt" or "l" => "lt",
            "set" => "set",
            _ => t
        };
    }

    // ------------------------------------------------------------------ //
    //  TAMAMLAMA
    // ------------------------------------------------------------------ //
    public async Task<ProgressPaymentCheckDto> FinalizeAsync(int checkId)
    {
        await RecalculateAsync(checkId);
        var check = await _db.ProgressPaymentChecks.FindAsync(checkId)
            ?? throw new InvalidOperationException("Kontrol kaydı bulunamadı.");
        check.Status = ProgressPaymentCheckStatus.Tamamlandi;
        check.CompletedAt = DateTime.Now;
        await _db.SaveChangesAsync();

        var items = await _db.ProgressPaymentCheckItems.Where(i => i.ProgressPaymentCheckId == checkId).ToListAsync();
        return MapCheck(check, items);
    }

    // ------------------------------------------------------------------ //
    //  EXPORT — kontrol edilmiş Excel kopyası
    // ------------------------------------------------------------------ //
    public async Task<string> ExportControlledExcelAsync(int checkId)
    {
        var check = await _db.ProgressPaymentChecks.FindAsync(checkId)
            ?? throw new InvalidOperationException("Kontrol kaydı bulunamadı.");
        var items = await _db.ProgressPaymentCheckItems
            .Where(i => i.ProgressPaymentCheckId == checkId)
            .ToListAsync();

        if (!File.Exists(check.OriginalFilePath))
            throw new InvalidOperationException("Orijinal hakediş dosyası bulunamadı: " + check.OriginalFilePath);

        using var wb = new XLWorkbook(check.OriginalFilePath); // orijinal dosya diskte değişmez, bu ayrı bir yeni dosyaya kaydedilecek

        var bySheet = items.Where(i => !string.IsNullOrEmpty(i.SheetName)).GroupBy(i => i.SheetName!);
        foreach (var sheetGroup in bySheet)
        {
            if (!wb.Worksheets.TryGetWorksheet(sheetGroup.Key, out var ws)) continue;

            int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
            var itemsByRow = sheetGroup.Where(i => i.SourceRowNumber.HasValue).ToDictionary(i => i.SourceRowNumber!.Value);
            if (itemsByRow.Count == 0) continue;

            int headerRow = itemsByRow.Keys.Min() - 1;
            int c0 = lastCol + 1;

            ws.Cell(headerRow, c0 + 0).Value = "ORİJİNAL MALZEME ADI";
            ws.Cell(headerRow, c0 + 1).Value = "EŞLEŞEN MALZEME";
            ws.Cell(headerRow, c0 + 2).Value = "KONTROL BİRİM FİYATI (TL)";
            ws.Cell(headerRow, c0 + 3).Value = "KULLANILAN KUR";
            ws.Cell(headerRow, c0 + 4).Value = "HESAPLANAN TUTAR";
            ws.Cell(headerRow, c0 + 5).Value = "FİRMA TUTARI";
            ws.Cell(headerRow, c0 + 6).Value = "FARK";
            ws.Cell(headerRow, c0 + 7).Value = "KONTROL DURUMU";
            ws.Cell(headerRow, c0 + 8).Value = "KONTROL NOTU";
            var headerRange = ws.Range(headerRow, c0, headerRow, c0 + 8);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.FromArgb(230, 230, 230);

            foreach (var (rowNo, item) in itemsByRow)
            {
                ws.Cell(rowNo, c0 + 0).Value = item.OriginalMaterialName;
                ws.Cell(rowNo, c0 + 1).Value = item.MatchedMaterialName ?? "";
                if (item.ApprovedUnitPriceTry.HasValue) ws.Cell(rowNo, c0 + 2).Value = item.ApprovedUnitPriceTry.Value;
                if (check.ExchangeRateEur.HasValue && item.ApprovedCurrency == "EUR") ws.Cell(rowNo, c0 + 3).Value = check.ExchangeRateEur.Value;
                if (item.CalculatedLineTotal.HasValue) ws.Cell(rowNo, c0 + 4).Value = item.CalculatedLineTotal.Value;
                ws.Cell(rowNo, c0 + 5).Value = item.CompanyLineTotal;
                if (item.Difference.HasValue) ws.Cell(rowNo, c0 + 6).Value = item.Difference.Value;
                ws.Cell(rowNo, c0 + 7).Value = ControlStatusLabel(item.ControlStatus);
                ws.Cell(rowNo, c0 + 8).Value = BuildExportNote(item); // yalnızca problemli satırlara not — Uygun satırlar boş kalır

                // ── Yanlış birim fiyat "Düzelt" ile onaylandıysa: firmanın kendi hücresini onaylı fiyatla
                // değiştir ve yalnızca bu hücreyi kırmızı işaretle. Formüller/diğer hücreler dokunulmaz.
                if (item.PriceCorrectionApplied && item.ApprovedUnitPriceTry.HasValue && !string.IsNullOrWhiteSpace(item.UnitPriceCellRef))
                {
                    try
                    {
                        var priceCell = ws.Cell(item.UnitPriceCellRef);
                        priceCell.Value = item.ApprovedUnitPriceTry.Value;
                        priceCell.Style.Fill.BackgroundColor = CorrectionFillColor;
                        priceCell.Style.Font.FontColor = CorrectionFontColor;
                        priceCell.Style.Font.Bold = true;
                    }
                    catch { /* hücre adresi çözülemedi — orijinal satır formatı beklenenden farklı, sessizce geç */ }
                }
            }
        }

        // Formül hücreleri (satır toplamı, KDV, genel toplam vb.) silinmez/statikleştirilmez — yalnızca
        // birim fiyat hücresi değişti; workbook Excel'de açıldığında otomatik yeniden hesaplansın diye
        // hesaplama modu Auto'ya ayarlanır ve mümkün olduğunca önceden yeniden hesaplanır.
        try { wb.CalculateMode = XLCalculateMode.Auto; } catch { /* kritik değil */ }
        try { wb.RecalculateAllFormulas(); } catch { /* bazı fonksiyonlar desteklenmeyebilir — dosya yine de geçerli kalır */ }

        var folder = GetControlledFolder(check.CompanyName, check.Year, check.Month);
        Directory.CreateDirectory(folder);
        var fileName = $"{Path.GetFileNameWithoutExtension(check.OriginalFileName)}_KONTROL_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var outPath = Path.Combine(folder, fileName);
        wb.SaveAs(outPath);

        check.ControlledFilePath = outPath;
        await _db.SaveChangesAsync();
        return outPath;
    }

    /// <summary>Export'ta sadece problemli satırlara not yazılır — Uygun satırların not hücresi boş kalır (firmaya gidecek dosyada gereksiz not olmaz).</summary>
    private static string BuildExportNote(ProgressPaymentCheckItem item) => item.ControlStatus switch
    {
        CheckItemControlStatus.Uygun => string.Empty,
        CheckItemControlStatus.FiyatHatasi => item.PriceCorrectionApplied
            ? $"Birim fiyat {item.CompanyUnitPrice:N2} TL yerine onaylı {item.ApprovedUnitPriceTry:N2} TL olarak düzeltilmiştir."
            : $"Firma birim fiyatı ({item.CompanyUnitPrice:N2} TL) onaylı birim fiyattan ({item.ApprovedUnitPriceTry:N2} TL) farklıdır. Kontrol edilmelidir.",
        CheckItemControlStatus.BirimFiyatBulunamadi => "Onaylı birim fiyat listesinde karşılığı bulunamadı. Kontrol edilmelidir.",
        CheckItemControlStatus.BirimUyusmazligi => "Hakedişteki birim ile onaylı birim fiyat birimi uyuşmamaktadır.",
        CheckItemControlStatus.OnayBekliyor => "Tahmini eşleşme kullanıcı onayı bekliyor. Manuel inceleme gereklidir.",
        CheckItemControlStatus.KontrolDisi => "Kullanıcı tarafından kontrol dışı bırakılmıştır.",
        _ => string.IsNullOrWhiteSpace(item.ControlNote) ? "Manuel inceleme gereken kalem." : item.ControlNote!,
    };

    private static string ControlStatusLabel(CheckItemControlStatus s) => s switch
    {
        CheckItemControlStatus.Uygun => "Uygun",
        CheckItemControlStatus.OnayBekliyor => "Onay Bekliyor",
        CheckItemControlStatus.FiyatHatasi => "Fiyat Hatası",
        CheckItemControlStatus.BirimFiyatBulunamadi => "Birim Fiyat Bulunamadı",
        CheckItemControlStatus.BirimUyusmazligi => "Birim Uyuşmazlığı",
        CheckItemControlStatus.KontrolDisi => "Kontrol Dışı",
        _ => "Kontrol Gerekli"
    };

    // ------------------------------------------------------------------ //
    //  DOSYA SAKLAMA
    // ------------------------------------------------------------------ //
    private string SaveOriginalFile(string companyName, int year, int month, string originalFileName, byte[] bytes)
    {
        var folder = Path.Combine(_appPath.DataRootPath, "SogutmaHakedisKontrol", year.ToString(), $"{month:00}", "Orijinal");
        Directory.CreateDirectory(folder);
        var safeName = string.Join("_", originalFileName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string GetControlledFolder(string companyName, int year, int month)
        => Path.Combine(_appPath.DataRootPath, "SogutmaHakedisKontrol", year.ToString(), $"{month:00}", "KontrolEdilmis");

    // ------------------------------------------------------------------ //
    //  MAPPING
    // ------------------------------------------------------------------ //
    private static ProgressPaymentCheckDto MapCheck(ProgressPaymentCheck c, List<ProgressPaymentCheckItem> items) => new()
    {
        Id = c.Id,
        UnitPriceListId = c.UnitPriceListId,
        CompanyName = c.CompanyName,
        Region = c.Region,
        ClaimTypeName = c.ClaimTypeName,
        Year = c.Year,
        Month = c.Month,
        PeriodLabel = c.PeriodLabel,
        OriginalFileName = c.OriginalFileName,
        OriginalFilePath = c.OriginalFilePath,
        ExchangeRateEur = c.ExchangeRateEur,
        CompanyTotal = c.CompanyTotal,
        CalculatedTotal = c.CalculatedTotal,
        Difference = c.Difference,
        Status = c.Status,
        CreatedAt = c.CreatedAt,
        CompletedAt = c.CompletedAt,
        ControlledFilePath = c.ControlledFilePath,
        TotalItemCount = items.Count,
        UygunCount = items.Count(i => i.ControlStatus == CheckItemControlStatus.Uygun),
        OnayBekliyorCount = items.Count(i => i.ControlStatus == CheckItemControlStatus.OnayBekliyor),
        HataliCount = items.Count(i => i.ControlStatus is CheckItemControlStatus.FiyatHatasi or CheckItemControlStatus.BirimUyusmazligi),
        EslesmeyenCount = items.Count(i => i.ControlStatus == CheckItemControlStatus.BirimFiyatBulunamadi),
    };

    private static ProgressPaymentCheckItemDto MapItem(ProgressPaymentCheckItem i) => new()
    {
        Id = i.Id,
        ProgressPaymentCheckId = i.ProgressPaymentCheckId,
        SheetName = i.SheetName,
        SourceRowNumber = i.SourceRowNumber,
        MaterialCellRef = i.MaterialCellRef,
        QuantityCellRef = i.QuantityCellRef,
        UnitPriceCellRef = i.UnitPriceCellRef,
        LineTotalCellRef = i.LineTotalCellRef,
        StoreCode = i.StoreCode,
        StoreName = i.StoreName,
        StoreFormat = i.StoreFormat,
        VisitDate = i.VisitDate,
        MaintenanceFormNo = i.MaintenanceFormNo,
        OriginalItemCode = i.OriginalItemCode,
        OriginalMaterialName = i.OriginalMaterialName,
        OriginalMaterialSpec = i.OriginalMaterialSpec,
        IsServiceItem = i.IsServiceItem,
        Quantity = i.Quantity,
        Unit = i.Unit,
        CompanyUnitPrice = i.CompanyUnitPrice,
        CompanyLineTotal = i.CompanyLineTotal,
        MatchedUnitPriceItemId = i.MatchedUnitPriceItemId,
        MatchedMaterialName = i.MatchedMaterialName,
        MatchConfidence = i.MatchConfidence,
        MatchStatus = i.MatchStatus,
        ApprovedUnitPrice = i.ApprovedUnitPrice,
        ApprovedCurrency = i.ApprovedCurrency,
        ApprovedUnitPriceTry = i.ApprovedUnitPriceTry,
        CalculatedLineTotal = i.CalculatedLineTotal,
        Difference = i.Difference,
        DifferencePercent = i.DifferencePercent,
        UnitMismatch = i.UnitMismatch,
        ControlStatus = i.ControlStatus,
        ControlNote = i.ControlNote,
        IsExcluded = i.IsExcluded,
        QuantityManuallyCorrected = i.QuantityManuallyCorrected,
        PriceCorrectionApplied = i.PriceCorrectionApplied,
    };
}
