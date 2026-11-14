using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Presence;

public class PresenceInfo
{
    public string SourceKey = "media";
    public string SourceName = "";
    public Color Accent = Color.FromRgb(0x34, 0xC7, 0x59);
}

public interface IPresenceProvider
{
    int Priority { get; }
    bool Matches(MediaInfo m);
    PresenceInfo Build(MediaInfo m);
}

/// <summary>Recognizes the active media session and creates its source badge
/// (Spotify, YouTube, browser, and so on). Add a provider file here for new sources.</summary>
public class PresenceManager
{
    private readonly List<IPresenceProvider> _providers;

    public PresenceManager()
    {
        _providers = new List<IPresenceProvider>
        {
            new SpotifyPresenceProvider(),
            new YouTubePresenceProvider(),
            new WebPresenceProvider(),
            new DefaultPresenceProvider()
        };
        _providers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    public PresenceInfo Resolve(MediaInfo m)
    {
        foreach (var p in _providers)
            if (p.Matches(m))
                return p.Build(m);
        return new PresenceInfo();
    }
}
