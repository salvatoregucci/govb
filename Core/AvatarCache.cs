using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Core;

/// <summary>Downloads and keeps small Discord avatar PNGs (~80 px) in memory.</summary>
public static class AvatarCache
{
    private static readonly HttpClient Http = new();
    private static readonly Dictionary<string, BitmapImage> Cache = new();
    private static readonly HashSet<string> InFlight = new();

    public static BitmapImage? TryGet(string url) => Cache.TryGetValue(url, out var img) ? img : null;

    public static async Task<BitmapImage?> LoadAsync(string url)
    {
        if (Cache.TryGetValue(url, out var cached)) return cached;
        if (!InFlight.Add(url)) return null; // A request for this URL is already in progress.

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            Cache[url] = img;
            return img;
        }
        catch { return null; }
        finally { InFlight.Remove(url); }
    }
}
