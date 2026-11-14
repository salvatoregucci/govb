using System.Diagnostics;
using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Presence;

public class YouTubePresenceProvider : IPresenceProvider
{
    private static readonly string[] BrowserProcs =
        { "brave", "chrome", "msedge", "firefox", "opera", "vivaldi", "zen" };

    private static DateTime _lastCheck = DateTime.MinValue;
    private static bool _lastResult;

    public int Priority => 90;

    public bool Matches(MediaInfo m)
    {
        if (m.Aumid.Contains("youtube", StringComparison.OrdinalIgnoreCase))
            return true;

        // For browser sessions, look for "YouTube" in the window title (5-second cache).
        var isBrowser = BrowserProcs.Any(b => m.Aumid.Contains(b, StringComparison.OrdinalIgnoreCase));
        if (!isBrowser) return false;

        if ((DateTime.Now - _lastCheck).TotalSeconds > 5)
        {
            _lastCheck = DateTime.Now;
            _lastResult = AnyBrowserWindowHasYouTube();
        }
        return _lastResult;
    }

    private static bool AnyBrowserWindowHasYouTube()
    {
        try
        {
            foreach (var name in BrowserProcs)
                foreach (var p in Process.GetProcessesByName(name))
                    using (p)
                        if (p.MainWindowTitle.Contains("youtube", StringComparison.OrdinalIgnoreCase))
                            return true;
        }
        catch { }
        return false;
    }

    public PresenceInfo Build(MediaInfo m) => new()
    {
        SourceKey = "youtube",
        SourceName = "YouTube",
        Accent = Color.FromRgb(0xFF, 0x00, 0x33)
    };
}
