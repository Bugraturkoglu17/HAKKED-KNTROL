namespace HakedisOtomasyon.Application.Interfaces;

public interface ISettingsService
{
    Task<string> GetAsync(string key, string defaultValue = "");
    Task<decimal> GetDecimalAsync(string key, decimal defaultValue = 0);
    Task SetAsync(string key, string value);
    Task EnsureDefaultSettingsAsync();
}
