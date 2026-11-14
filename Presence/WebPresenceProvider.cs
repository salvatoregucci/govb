using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Presence;

/// <summary>Media originating from browser tabs such as YouTube or SoundCloud.</summary>
public class WebPresenceProvider : IPresenceProvider
{
    private static readonly (string token, string name)[] Browsers =
    {
        ("brave", "Brave"), ("chrome", "Chrome"), ("msedge", "Edge"),
        ("edge", "Edge"), ("firefox", "Firefox"), ("opera", "Opera"),
        ("vivaldi", "Vivaldi"), ("zen", "Zen")
    };

    public int Priority => 50;

    public bool Matches(MediaInfo m) => Find(m.Aumid) != null;

    public PresenceInfo Build(MediaInfo m) => new()
    {
        SourceKey = "web",
        SourceName = Find(m.Aumid) ?? "Web",
        Accent = Color.FromRgb(0xFF, 0x45, 0x3A)
    };

    private static string? Find(string aumid)
    {
        foreach (var (token, name) in Browsers)
            if (aumid.Contains(token, StringComparison.OrdinalIgnoreCase))
                return name;
        return null;
    }
}
