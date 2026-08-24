using HakedisOtomasyon.Application.Interfaces;
using HakedisOtomasyon.Domain.Entities;
using HakedisOtomasyon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HakedisOtomasyon.Infrastructure.Services;

public class SettingsService : ISettingsService
{
    private readonly AppDbContext _db;

    public SettingsService(AppDbContext db) => _db = db;

    public async Task<string> GetAsync(string key, string defaultValue = "")
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value ?? defaultValue;
    }

    public async Task<decimal> GetDecimalAsync(string key, decimal defaultValue = 0)
    {
        var val = await GetAsync(key);
        return decimal.TryParse(val, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result : defaultValue;
    }

    public async Task SetAsync(string key, string value)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
        await _db.SaveChangesAsync();
    }

    public async Task EnsureDefaultSettingsAsync()
    {
        var defaults = new Dictionary<string, (string Value, string Description)>
        {
            [SettingKeys.DefaultServiceFee] = ("4550", "Varsayılan servis ücreti (TL)"),
            [SettingKeys.InvoiceMarkupRate] = ("0.10", "Fatura üzerine eklenecek oran (ondalık, örn: 0.10 = %10)"),
            [SettingKeys.CompanyName] = ("", "Excel başlığında görünecek şirket adı"),
            [SettingKeys.CompanyAddress] = ("", "Şirket adresi"),
            [SettingKeys.CompanyPhone] = ("", "Şirket telefon numarası"),
            [SettingKeys.ExcelOutputPath] = ("", "Excel çıktı klasörü (boş ise varsayılan kullanılır)"),
            [SettingKeys.ThemeMode] = ("light", "Tema modu: light veya dark"),
            [SettingKeys.ArchiveRetentionDays] = ("30", "Arşivde saklanacak gün sayısı (0 = süresiz)"),
            [SettingKeys.ExcelImageQuality] = ("standard", "Excel görsel kalitesi: low | standard | high"),
        };

        foreach (var (key, (value, desc)) in defaults)
        {
            if (!await _db.AppSettings.AnyAsync(s => s.Key == key))
            {
                _db.AppSettings.Add(new AppSetting { Key = key, Value = value, Description = desc });
            }
        }
        await _db.SaveChangesAsync();
    }
}
