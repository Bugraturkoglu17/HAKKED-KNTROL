using System.Text.Json;
using System.Text.Json.Serialization;

namespace HakedisOtomasyon.Infrastructure.FileStorage;

/// <summary>
/// AppData\Local\HakedisOtomasyon\config.json içindeki yapılandırmayı temsil eder.
/// Yalnızca veritabanı dışında saklanması gereken küçük ayarlar (DataRootPath gibi) buraya yazılır.
/// </summary>
public class AppDataConfig
{
    [JsonPropertyName("dataRootPath")]
    public string DataRootPath { get; set; } = string.Empty;

    [JsonPropertyName("lastOpenedDate")]
    public string LastOpenedDate { get; set; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "1.0.1";
}

/// <summary>
/// AppData\Local\HakedisOtomasyon\config.json dosyasını okur ve yazar.
/// </summary>
public static class AppDataSettings
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HakedisOtomasyon");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>Yapılandırmayı yükler. Dosya yoksa veya bozuksa varsayılan döner.</summary>
    public static AppDataConfig Load()
    {
        if (!File.Exists(ConfigFile))
            return new AppDataConfig();

        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<AppDataConfig>(json, JsonOpts) ?? new AppDataConfig();
        }
        catch
        {
            return new AppDataConfig();
        }
    }

    /// <summary>Yapılandırmayı diske kaydeder.</summary>
    public static void Save(AppDataConfig config)
    {
        config.LastOpenedDate = DateTime.Now.ToString("yyyy-MM-dd");
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(ConfigFile, json);
    }
}
