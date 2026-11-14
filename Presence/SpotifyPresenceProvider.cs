using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Presence;

public class SpotifyPresenceProvider : IPresenceProvider
{
    public int Priority => 100;

    public bool Matches(MediaInfo m) =>
        m.Aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    public PresenceInfo Build(MediaInfo m) => new()
    {
        SourceKey = "spotify",
        SourceName = "Spotify",
        Accent = Color.FromRgb(0x1D, 0xB9, 0x54)
    };
}
