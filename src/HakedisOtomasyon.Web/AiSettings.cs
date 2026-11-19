using System.IO;
using System.Net.Http;

namespace HakedisOtomasyon.Web;

/// <summary>
/// Servis formu okuma (Otomatik doldur) için yapay zekâ ayarları: sağlayıcı (Gemini / Azure), Gemini modeli ve anahtarlar.
/// Değerler programın klasöründeki <c>.env</c> dosyasında tutulur (firma Ayarlar ekranından kendi anahtarını girer) ve kaydedilince
/// çalışan programda da hemen geçerli olur. Okurken önce süreç ortamına, yoksa Windows kullanıcı ortam değişkenine bakılır.
/// </summary>
public static class AiSettings
{
    public const string ProviderVar = "FORM_READER";
    public const string GeminiKeyVar = "GEMINI_API_KEY";
    public const string GeminiModelVar = "GEMINI_MODEL";
    public const string AzureEndpointVar = "AZURE_DI_ENDPOINT";
    public const string AzureKeyVar = "AZURE_DI_KEY";

    public const string Gemini = "gemini";
    public const string Azure = "azure";
    public const string DefaultGeminiModel = "gemini-3.5-flash-lite";

    /// <summary>Ayarlar ekranında seçilebilen Gemini modelleri (en ucuzdan en güçlüye).</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> GeminiModels = new[]
    {
        ("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite — önerilen (hızlı, en ucuz)"),
        ("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite"),
        ("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite"),
        ("gemini-3.6-flash", "Gemini 3.6 Flash (daha güçlü, daha pahalı)"),
        ("gemini-3.8-flash", "Gemini 3.8 Flash"),
        ("gemini-3.1-pro", "Gemini 3.1 Pro (en güçlü, en pahalı)"),
    };

    public static string EnvFilePath => Path.Combine(AppContext.BaseDirectory, ".env");

    public static string? Get(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        v = v?.Trim();
        return string.IsNullOrEmpty(v) || v.StartsWith("BURAYA_", StringComparison.OrdinalIgnoreCase) ? null : v;
    }

    public static bool GeminiConfigured => Get(GeminiKeyVar) is not null;
    public static bool AzureConfigured => Get(AzureEndpointVar) is not null && Get(AzureKeyVar) is not null;

    /// <summary>Seçili sağlayıcı. Açıkça seçilmemişse: Azure ayarlıysa Azure, değilse Gemini.</summary>
    public static string Provider => Get(ProviderVar)?.ToLowerInvariant() switch
    {
        Azure => Azure,
        Gemini => Gemini,
        _ => AzureConfigured && !GeminiConfigured ? Azure : Gemini,
    };

    public static string GeminiModel => Get(GeminiModelVar) ?? DefaultGeminiModel;

    /// <summary>Ekranda küçük yazılan açıklama: hangi yapay zekâ ile okunduğu.</summary>
    public static string Describe() => Provider == Azure
        ? "Azure Document Intelligence"
        : $"Gemini · {GeminiModel}";

    /// <summary>Anahtarın yalnız son 4 karakteri (ekranda gösterim için).</summary>
    public static string? Masked(string name) => Get(name) is { Length: > 4 } v ? "••••" + v[^4..] : Get(name) is null ? null : "••••";

    /// <summary>
    /// Değerleri .env dosyasına yazar (satır varsa değiştirir, yoksa ekler) ve çalışan programda hemen geçerli kılar. Değeri null olan
    /// anahtara dokunulmaz. Aynı ad Windows kullanıcı ortam değişkeni olarak da tanımlıysa o da güncellenir (yoksa açılışta .env'i ezerdi).
    /// </summary>
    public static void Save(IReadOnlyDictionary<string, string?> values)
    {
        var lines = File.Exists(EnvFilePath) ? File.ReadAllLines(EnvFilePath).ToList() : new List<string>();
        foreach (var (name, value) in values)
        {
            if (value is null) continue;
            var v = value.Trim();
            var index = lines.FindIndex(l => l.TrimStart().StartsWith(name + "=", StringComparison.Ordinal));
            if (index >= 0) lines[index] = $"{name}={v}";
            else lines.Add($"{name}={v}");

            Environment.SetEnvironmentVariable(name, v);
            if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is not null)
                Environment.SetEnvironmentVariable(name, v, EnvironmentVariableTarget.User);
        }
        File.WriteAllLines(EnvFilePath, lines);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Gemini anahtarı ve modeli geçerli mi (modelin bilgisini sorgular; ücret çıkmaz).</summary>
    public static async Task<(bool Ok, string Message)> TestGeminiAsync(string key, string model)
    {
        try
        {
            using var r = await Http.GetAsync($"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}?key={Uri.EscapeDataString(key)}");
            return (int)r.StatusCode switch
            {
                200 => (true, $"Bağlantı başarılı: {model}"),
                400 or 401 or 403 => (false, "Anahtar geçersiz ya da bu anahtarın Gemini API yetkisi yok."),
                404 => (false, $"\"{model}\" modeli bulunamadı; model adını kontrol edin."),
                429 => (false, "İstek sınırı doldu; birazdan tekrar deneyin."),
                _ => (false, $"Gemini yanıt vermedi (HTTP {(int)r.StatusCode})."),
            };
        }
        catch (Exception ex) { return (false, $"Gemini'ye bağlanılamadı: {ex.Message}"); }
    }

    /// <summary>Azure Document Intelligence adresi ve anahtarı geçerli mi (model bilgisini sorgular; ücret çıkmaz).</summary>
    public static async Task<(bool Ok, string Message)> TestAzureAsync(string endpoint, string key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{endpoint.Trim().TrimEnd('/')}/documentintelligence/documentModels/prebuilt-layout?api-version=2024-11-30");
            req.Headers.Add("Ocp-Apim-Subscription-Key", key.Trim());
            using var r = await Http.SendAsync(req);
            return (int)r.StatusCode switch
            {
                200 => (true, "Bağlantı başarılı: Azure Document Intelligence"),
                401 or 403 => (false, "Azure anahtarı geçersiz."),
                404 => (false, "Adres yanlış (Document Intelligence kaynağının Endpoint değerini girin)."),
                _ => (false, $"Azure yanıt vermedi (HTTP {(int)r.StatusCode})."),
            };
        }
        catch (Exception ex) { return (false, $"Azure'a bağlanılamadı: {ex.Message}"); }
    }
}
