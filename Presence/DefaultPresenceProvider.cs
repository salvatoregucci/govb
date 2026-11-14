using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Presence;

public class DefaultPresenceProvider : IPresenceProvider
{
    public int Priority => 0;

    public bool Matches(MediaInfo m) => true;

    public PresenceInfo Build(MediaInfo m) => new()
    {
        SourceKey = "media",
        SourceName = CleanName(m.Aumid),
        Accent = Color.FromRgb(0x8E, 0x8E, 0x93)
    };

    private static string CleanName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "Medya";
        var t = aumid;
        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
        var bang = t.IndexOf('!');
        if (bang > 0) t = t[..bang];
        var parts = t.Split('.', '_');
        return parts.Length > 0 ? parts[^1] : t;
    }
}
