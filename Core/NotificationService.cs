using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Core;

public class ToastInfo
{
    public string App = "";
    public string Title = "";
    public string Body = "";
}

/// <summary>Listens to Windows Notification Center through UserNotificationListener.
/// Unpackaged apps may require the user to grant access in Windows Settings.</summary>
public class NotificationService : IDisposable
{
    private UserNotificationListener? _listener;
    private readonly HashSet<uint> _seen = new();
    private bool _primed;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private int _polling;

    public bool Available { get; private set; }
    public event Action<ToastInfo>? Toast;

    public async Task InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            Available = access == UserNotificationListenerAccessStatus.Allowed;
        }
        catch
        {
            Available = false;
            return;
        }
        if (!Available) return;

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2000)
        };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_listener == null || Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            var notifs = await _listener.GetNotificationsAsync(NotificationKinds.Toast);

            if (!_primed)
            {
                // Treat existing notifications as seen on the first pass to avoid a burst.
                foreach (var n in notifs) _seen.Add(n.Id);
                _primed = true;
                return;
            }

            foreach (var n in notifs)
            {
                if (!_seen.Add(n.Id)) continue;

                var info = new ToastInfo();
                try { info.App = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                try
                {
                    var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding != null)
                    {
                        var texts = binding.GetTextElements();
                        if (texts.Count > 0) info.Title = texts[0].Text;
                        if (texts.Count > 1)
                            info.Body = string.Join(" · ", texts.Skip(1).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                    }
                }
                catch { }

                if (info.Title.Length > 0 || info.Body.Length > 0)
                    Toast?.Invoke(info);
            }

            // Do not retain IDs for dismissed notifications forever.
            if (_seen.Count > 512)
            {
                var current = notifs.Select(n => n.Id).ToHashSet();
                _seen.RemoveWhere(id => !current.Contains(id));
            }
        }
        catch { /* bildirim merkezi geçici hata verebilir */ }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _listener = null;
        _seen.Clear();
    }
}
