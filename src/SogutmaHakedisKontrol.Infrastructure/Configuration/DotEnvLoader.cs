namespace SogutmaHakedisKontrol.Infrastructure.Configuration;

/// <summary>
/// Basit ".env" yükleyici — geliştirici makinesinde OPENAI_API_KEY gibi değerleri gerçek ortam
/// değişkeni olarak ayarlamadan çalışabilmek için. Uygulama klasöründeki ".env" dosyasını okur.
/// Zaten ayarlanmış bir ortam değişkeninin üzerine ASLA yazmaz (gerçek ortam/hosting secrets önceliklidir).
/// API anahtarı hiçbir zaman loglanmaz.
/// </summary>
public static class DotEnvLoader
{
    public static void Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, ".env");
        if (!File.Exists(path)) return;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim().Trim('"');

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
