using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Core;

/// <summary>Resolves the executable icon of the running process from an SMTC AUMID
/// such as "Spotify.exe", "Brave", or "MSEdge".</summary>
public static class AppIconResolver
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Resolve(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        if (Cache.TryGetValue(aumid, out var cached)) return cached;

        ImageSource? result = null;
        try
        {
            var token = NormalizeToken(aumid);
            var path = FindProcessPath(token);
            if (path != null)
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    result = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    result.Freeze();
                }
            }
        }
        catch { result = null; }

        Cache[aumid] = result;
        return result;
    }

    private static string NormalizeToken(string aumid)
    {
        var t = aumid.ToLowerInvariant();
        if (t.EndsWith(".exe")) t = t[..^4];
        // Packaged app AUMID: "Publisher.App_hash!App"; keep the application-name portion.
        var bang = t.IndexOf('!');
        if (bang > 0) t = t[..bang];
        var underscore = t.IndexOf('_');
        if (underscore > 0) t = t[..underscore];
        var dot = t.LastIndexOf('.');
        if (dot > 0 && dot < t.Length - 1) t = t[(dot + 1)..];
        return t;
    }

    private static string? FindProcessPath(string token)
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                    token.Contains(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    var path = p.MainModule?.FileName;
                    if (path != null) return path;
                }
            }
            catch { /* erişilemeyen süreçler (yükseltilmiş vb.) */ }
            finally { p.Dispose(); }
        }
        return null;
    }
}
