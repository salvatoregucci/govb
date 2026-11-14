using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Core;

/// <summary>Extracts a vivid accent colour from album art for the adaptive theme.</summary>
public static class AlbumColorExtractor
{
    public static Color? Extract(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 48;
                img.StreamSource = ms;
                img.EndInit();
            }
            var conv = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            // Use a weighted average of saturated, bright pixels.
            double rSum = 0, gSum = 0, bSum = 0, wSum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int sat = max - min;
                if (max < 40) continue; // Nearly black.

                double weight = sat * (max / 255.0);
                if (weight <= 0) continue;
                rSum += r * weight; gSum += g * weight; bSum += b * weight;
                wSum += weight;
            }
            if (wSum < 1) return null; // Fully grey or black artwork.

            var c = Color.FromRgb(
                (byte)(rSum / wSum), (byte)(gSum / wSum), (byte)(bSum / wSum));
            return Vivid(c);
        }
        catch { return null; }
    }

    /// <summary>Makes a colour vivid and bright enough to remain legible on black.</summary>
    private static Color Vivid(Color c)
    {
        RgbToHsv(c, out var hue, out var sat, out var val);
        sat = Math.Max(sat, 0.55);
        val = Math.Max(val, 0.85);
        return HsvToRgb(hue, sat, val);
    }

    private static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb(
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
