using SkiaSharp;

namespace HakedisOtomasyon.Infrastructure.ExcelExport;

/// <summary>
/// Excel'e eklenecek görselleri okunurluğu koruyarak sıkıştırır.
/// SkiaSharp kullanır — Windows, Linux ve macOS desteklenir.
/// </summary>
internal static class ImageOptimizer
{
    /// <summary>
    /// Görsel kalite ön ayarları.
    /// </summary>
    public static class Preset
    {
        /// <summary>Düşük Boyut — 1200 px, JPEG kalite 70</summary>
        public static readonly ImageQualityOptions Low         = new(1200, 70);
        /// <summary>Standart — 1600 px, JPEG kalite 80 (varsayılan)</summary>
        public static readonly ImageQualityOptions Standard    = new(1600, 80);
        /// <summary>Yüksek Okunurluk — 2000 px, JPEG kalite 85</summary>
        public static readonly ImageQualityOptions High        = new(2000, 85);
        /// <summary>Teknik Tablo (BDKF/BDTX/FLEXIVA) — 1800 px, JPEG kalite 85</summary>
        public static readonly ImageQualityOptions TechnicalTable = new(1800, 85);

        public static ImageQualityOptions FromSettingValue(string setting) => setting switch
        {
            "low"  => Low,
            "high" => High,
            _      => Standard
        };
    }

    /// <summary>
    /// Görseli yeniden boyutlandırır ve JPEG olarak sıkıştırır.
    /// <paramref name="imageBytes"/> herhangi bir SkiaSharp'ın desteklediği
    /// formatta olabilir (JPEG, PNG, WebP, BMP…).
    /// Görsel <paramref name="options.MaxWidthPx"/> pikselinden geniş değilse
    /// yeniden boyutlandırma yapılmaz; yalnızca JPEG sıkıştırması uygulanır.
    /// </summary>
    public static byte[] Optimize(byte[] imageBytes, ImageQualityOptions options)
    {
        try
        {
            using var original = SKBitmap.Decode(imageBytes);
            if (original is null) return imageBytes;

            SKBitmap target;
            if (original.Width > options.MaxWidthPx)
            {
                float scale  = (float)options.MaxWidthPx / original.Width;
                int newH     = (int)Math.Round(original.Height * scale);
                var info     = new SKImageInfo(options.MaxWidthPx, newH, original.ColorType, original.AlphaType);
                target       = new SKBitmap(info);
                original.ScalePixels(target, SKFilterQuality.High);
            }
            else
            {
                target = original;
            }

            using var image   = SKImage.FromBitmap(target);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, options.JpegQuality);

            if (target != original) target.Dispose();

            return encoded.ToArray();
        }
        catch
        {
            // Optimizasyon başarısız olursa orijinal baytlar döndürülür
            return imageBytes;
        }
    }
}

/// <summary>Görsel kalite parametreleri.</summary>
internal sealed record ImageQualityOptions(int MaxWidthPx, int JpegQuality);
