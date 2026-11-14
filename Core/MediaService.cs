using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Core;

public class MediaInfo
{
    public bool Active;
    public string Title = "";
    public string Artist = "";
    public string Aumid = "";
    public bool Playing;
    public double Pos;
    public double Dur;
    /// <summary>Time when SMTC last updated Pos. Live position equals Pos plus the
    /// elapsed time since PosAt; browsers do not continuously refresh Pos.</summary>
    public DateTime PosAt = DateTime.MinValue;
    public byte[]? Art;
    public DateTime Updated = DateTime.MinValue;
}

public class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private string _artKey = "";
    private byte[]? _artCache;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private int _polling;

    public MediaInfo Info { get; private set; } = new();
    public event Action<MediaInfo>? MediaUpdated;

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch { return; }

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        var info = new MediaInfo { Updated = DateTime.Now };
        try
        {
            var s = _mgr?.GetCurrentSession();
            if (s != null)
            {
                var props = await s.TryGetMediaPropertiesAsync();
                var pb = s.GetPlaybackInfo();
                var tl = s.GetTimelineProperties();

                info.Active = !string.IsNullOrWhiteSpace(props.Title);
                info.Title = props.Title ?? "";
                info.Artist = props.Artist ?? "";
                info.Aumid = s.SourceAppUserModelId ?? "";
                info.Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                info.Pos = tl.Position.TotalSeconds;
                info.Dur = tl.EndTime.TotalSeconds;
                var lu = tl.LastUpdatedTime.LocalDateTime;
                info.PosAt = lu.Year > 2015 ? lu : info.Updated;

                var key = info.Title + "|" + info.Artist;
                if (key != _artKey)
                {
                    _artKey = key;
                    _artCache = await ReadThumbAsync(props.Thumbnail);
                }
                info.Art = _artCache;
            }
        }
        catch { /* oturum kapanırken COM hataları normal */ }

        Interlocked.Exchange(ref _polling, 0);
        Info = info;
        MediaUpdated?.Invoke(info);
    }

    private static async Task<byte[]?> ReadThumbAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return null;
        try
        {
            using var ras = await thumb.OpenReadAsync();
            var reader = new DataReader(ras.GetInputStreamAt(0));
            var size = (uint)ras.Size;
            if (size == 0 || size > 4_000_000) return null;
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    public async void PlayPause()
    {
        try { var s = _mgr?.GetCurrentSession(); if (s != null) await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { var s = _mgr?.GetCurrentSession(); if (s != null) await s.TrySkipNextAsync(); } catch { }
    }

    public async void Prev()
    {
        try { var s = _mgr?.GetCurrentSession(); if (s != null) await s.TrySkipPreviousAsync(); } catch { }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _mgr = null;
    }
}
