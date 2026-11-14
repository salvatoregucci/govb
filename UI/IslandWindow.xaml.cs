using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;
using Path = System.IO.Path;

namespace DynamicIsland.UI;

public partial class IslandWindow : Window
{
    private enum IslandState { Pill, Expanded, Notif }

    // State dimensions (DIP).
    private const double PillH = 40;
    private const double PillIdleW = 170;
    private const double PillMediaW = 300;
    private const int DiscordMaxShown = 11;
    private const double ExpW = 520, ExpH = 238;
    private const double NotifW = 420, NotifH = 100;

    private IslandState _state = IslandState.Pill;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _notifTimer = new() { Interval = TimeSpan.FromMilliseconds(4600) };
    private readonly DispatcherTimer _pillFlashTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private MediaInfo _media = new();
    private readonly Random _rng = new();
    private readonly float[] _targets = new float[AudioVisualizerService.BarCount];
    private readonly float[] _disp = new float[AudioVisualizerService.BarCount];
    private readonly List<Border> _pillBars = new();
    private string _artKeyShown = "";
    private DateTime _lastFrame = DateTime.Now;
    private DateTime _lastFft = DateTime.MinValue;
    private int _lastShownSec = -1;
    private bool _pillWide;
    private DiscordMember? _discordSpeaker;
    private bool _pillFlashActive;

    // DynamicWin-style view pager: Media / Calendar / File Tray / Bluetooth.
    private Grid[] _views = null!;
    private string[] _viewKeys = null!;
    private int _viewIndex;
    private DateTime _lastWheel = DateTime.MinValue;
    private DateTime _calMonth = DateTime.Today;
    private readonly DispatcherTimer _btTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    // Music-adaptive theme: all bars share this brush.
    private static readonly Color DefaultAccent = Color.FromRgb(0x34, 0xC7, 0x59);
    private readonly SolidColorBrush _accentBrush = new(DefaultAccent);

    public IslandWindow()
    {
        InitializeComponent();
        _views = new[] { ViewMedia, ViewCalendar, ViewTray, ViewBt, ViewDiscord };
        _viewKeys = new[] { "view.media", "view.calendar", "view.tray", "view.bt", "view.discord" };
        foreach (var v in _views) v.RenderTransform = new TranslateTransform();

        BuildBars();
        BuildDots();
        PositionWindow();

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); SetState(IslandState.Expanded); };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (_state == IslandState.Expanded) SetState(IslandState.Pill); };
        _notifTimer.Tick += (_, _) =>
        {
            _notifTimer.Stop();
            SetState(Island.IsMouseOver ? IslandState.Expanded : IslandState.Pill);
        };
        _btTimer.Tick += async (_, _) => { if (_viewIndex == 3 && _state == IslandState.Expanded) await RefreshBtAsync(); };
        _pillFlashTimer.Tick += (_, _) => { _pillFlashTimer.Stop(); _pillFlashActive = false; RefreshPill(); };

        CompositionTarget.Rendering += (_, _) => OnFrame();

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => UpdateClock();
        clock.Start();
        UpdateClock();

        App.Media.MediaUpdated += OnMedia;
        App.Notifications.Toast += t => Dispatcher.Invoke(() => ShowToast(t));
        App.Settings.Changed += () => Dispatcher.Invoke(OnSettingsChanged);
        App.Theme.Changed += () => Dispatcher.Invoke(() => ApplyState(false));
        App.Loc.Changed += () => Dispatcher.Invoke(ApplyTexts);
        App.FileTray.Changed += () => Dispatcher.Invoke(RefreshTray);
        App.Discord.Changed += () => Dispatcher.Invoke(OnDiscordChanged);
        App.Discord.MemberJoined += m => Dispatcher.Invoke(() => OnDiscordMemberJoined(m));

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(PositionWindow);

        StartDotPulse();
        ApplyState(false);
        ApplyTexts();
        BuildCalendar();
        RefreshTray();

        // Visual testing: --expand opens the panel and --view N selects a view.
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--expand"))
            Loaded += (_, _) =>
            {
                SetState(IslandState.Expanded);
                var vi = Array.IndexOf(args, "--view");
                if (vi >= 0 && vi + 1 < args.Length && int.TryParse(args[vi + 1], out var n))
                    SwitchView(n, animate: false);
            };
    }

    // ---------- Window ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Do not take focus or appear in Alt+Tab.
        var h = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---------- State machine ----------

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveTimer.Stop();
        if (_state == IslandState.Pill) _hoverTimer.Start();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_state == IslandState.Expanded) _leaveTimer.Start();
    }

    private void SetState(IslandState s)
    {
        if (_state == s) return;
        var old = _state;
        _state = s;

        // On every pill-to-panel transition, choose the most relevant tab again:
        // Media while playing; otherwise Discord when the user is in a voice channel.
        if (s == IslandState.Expanded && old == IslandState.Pill)
        {
            var target = ComputeDefaultView();
            if (target != _viewIndex) SwitchView(target, animate: false);
        }

        SwapView(RootOf(old), RootOf(s));
        ApplyState(true);
        if (s == IslandState.Expanded) OnViewShown();
    }

    private int ComputeDefaultView()
    {
        if (_media.Active) return 0; // Media is active.
        if (App.Discord.State.InChannel && App.Settings.Current.ShowViewDiscord) return 4; // In a voice channel.
        return 0;
    }

    private Grid RootOf(IslandState s) => s switch
    {
        IslandState.Expanded => ExpandedView,
        IslandState.Notif => NotifView,
        _ => PillView
    };

    private void ApplyState(bool animate)
    {
        double w, h, r;
        switch (_state)
        {
            case IslandState.Expanded: w = ExpW; h = ExpH; r = 30; break;
            case IslandState.Notif: w = NotifW; h = NotifH; r = 26; break;
            default:
                w = _pillWide ? PillMediaW : PillIdleW;
                h = PillH; r = PillH / 2;
                break;
        }

        var attach = App.Theme.AttachTop;
        Island.CornerRadius = new CornerRadius(attach ? 0 : r, attach ? 0 : r, r, r);

        if (animate)
        {
            AnimateSize(w, h, _state != IslandState.Pill);
        }
        else
        {
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.Width = w;
            Island.Height = h;
        }
    }

    private void AnimateSize(double w, double h, bool spring)
    {
        IEasingFunction ease = spring
            ? new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(spring ? 400 : 300);

        Island.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
    }

    private static void SwapView(Grid from, Grid to)
    {
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fadeOut.Completed += (_, _) => { if (to.Opacity > 0.5) from.Visibility = Visibility.Collapsed; };
        from.BeginAnimation(OpacityProperty, fadeOut);

        to.Visibility = Visibility.Visible;
        to.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(120) });
    }

    // ---------- View pager ----------

    /// <summary>Tabs enabled in Settings; Media is always included.</summary>
    private List<int> EnabledViews()
    {
        var s = App.Settings.Current;
        var list = new List<int> { 0 };
        if (s.ShowViewCalendar) list.Add(1);
        if (s.ShowViewTray) list.Add(2);
        if (s.ShowViewBt) list.Add(3);
        if (s.ShowViewDiscord) list.Add(4);
        return list;
    }

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state != IslandState.Expanded) return;
        if ((DateTime.Now - _lastWheel).TotalMilliseconds < 180) return;
        _lastWheel = DateTime.Now;

        var enabled = EnabledViews();
        if (enabled.Count < 2) return;
        var pos = Math.Max(0, enabled.IndexOf(_viewIndex));
        var dir = e.Delta < 0 ? 1 : -1;
        SwitchView(enabled[((pos + dir) % enabled.Count + enabled.Count) % enabled.Count]);
    }

    private void SwitchView(int idx, bool animate = true)
    {
        idx = ((idx % _views.Length) + _views.Length) % _views.Length;
        if (idx == _viewIndex) return;

        var dir = idx > _viewIndex || (idx == 0 && _viewIndex == _views.Length - 1) ? 1 : -1;
        if (idx == _views.Length - 1 && _viewIndex == 0) dir = -1;

        var from = _views[_viewIndex];
        var to = _views[idx];
        _viewIndex = idx;

        if (animate)
        {
            // Spring slide: old view exits while the new one enters with a subtle bounce.
            var fromTx = (TranslateTransform)from.RenderTransform;
            fromTx.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-40 * dir, TimeSpan.FromMilliseconds(140))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) => from.Visibility = Visibility.Collapsed;
            from.BeginAnimation(OpacityProperty, fade);

            to.Visibility = Visibility.Visible;
            var toTx = (TranslateTransform)to.RenderTransform;
            toTx.X = 50 * dir;
            toTx.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(340))
                { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
            to.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(60) });
        }
        else
        {
            from.Visibility = Visibility.Collapsed;
            from.Opacity = 0;
            to.Visibility = Visibility.Visible;
            to.Opacity = 1;
            ((TranslateTransform)to.RenderTransform).X = 0;
        }

        UpdateDots();
        UpdateViewHeader();
        OnViewShown();
    }

    private async void OnViewShown()
    {
        switch (_viewIndex)
        {
            case 1: BuildCalendar(); break;
            case 3: await RefreshBtAsync(); _btTimer.Start(); break;
            case 4: RefreshDiscordView(); break;
        }
        if (_viewIndex != 3) _btTimer.Stop();
    }

    private readonly List<(Ellipse Dot, int View)> _dotMap = new();

    private void BuildDots()
    {
        _dotMap.Clear();
        DotsPanel.Children.Clear();
        var enabled = EnabledViews();
        DotsPanel.Visibility = enabled.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var view in enabled)
        {
            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = Brushes.White,
                Opacity = 0.3,
                Cursor = Cursors.Hand
            };
            var target = view;
            dot.MouseLeftButtonDown += (_, _) => SwitchView(target);
            _dotMap.Add((dot, view));
            DotsPanel.Children.Add(dot);
        }
        UpdateDots();
    }

    private void UpdateDots()
    {
        foreach (var (dot, view) in _dotMap)
            dot.Opacity = view == _viewIndex ? 0.95 : 0.3;
    }

    private void UpdateViewHeader()
    {
        // Use the presence badge on Media; otherwise show the current view name.
        var media = _viewIndex == 0;
        ViewTitle.Visibility = media ? Visibility.Collapsed : Visibility.Visible;
        ViewTitle.Text = App.Loc.T(_viewKeys[_viewIndex]);
        PresenceBadge.Visibility = media && _media.Active ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Clock ----------

    private void UpdateClock()
    {
        var c = App.Loc.Culture;
        var now = DateTime.Now;
        var t = now.ToString("HH:mm");
        PillClock.Text = t;
        BigClock.Text = t;
        BigDate.Text = c.TextInfo.ToTitleCase(now.ToString("dddd, d MMMM yyyy", c));
        UpdateBattery();
    }

    private void UpdateBattery()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        var hasBattery =
            (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 &&
            ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;

        if (!hasBattery || !App.Settings.Current.ShowBattery)
        {
            HeaderBattery.Visibility = Visibility.Collapsed;
            return;
        }

        var pct = (int)Math.Round(ps.BatteryLifePercent * 100);
        var charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

        HeadBatText.Text = $"%{pct}" + (charging ? " ⚡" : "");
        HeadBatFill.Width = Math.Max(1.5, 16.5 * pct / 100.0);
        HeadBatFill.Background = pct <= 20 && !charging
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))
            : new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        HeaderBattery.Visibility = Visibility.Visible;
    }

    // ---------- Media ----------

    private void OnMedia(MediaInfo m)
    {
        _media = m;
        var s = App.Settings.Current;

        if (m.Active)
        {
            MediaTitle.Text = m.Title;
            MediaArtist.Text = m.Artist;
        }
        else
        {
            MediaTitle.Text = App.Loc.T("media.none");
            MediaArtist.Text = App.Loc.T("media.hint");
        }

        BtnPlay.Content = m.Playing ? "" : "";
        BtnPrev.IsEnabled = BtnPlay.IsEnabled = BtnNext.IsEnabled = m.Active;

        // Source application icon.
        ImageSource? appIcon = (m.Active && s.ShowMediaIcon) ? AppIconResolver.Resolve(m.Aumid) : null;
        PillAppIcon.Source = appIcon;
        PillAppIcon.Visibility = appIcon != null ? Visibility.Visible : Visibility.Collapsed;
        MediaAppIcon.Source = appIcon;
        MediaAppIconHolder.Visibility = appIcon != null ? Visibility.Visible : Visibility.Collapsed;

        // Album art and dominant colour.
        var artKey = m.Title + "|" + m.Artist + "|" + (m.Art?.Length ?? 0);
        if (artKey != _artKeyShown)
        {
            _artKeyShown = artKey;
            MediaArt.Source = m.Art != null ? ToImage(m.Art) : null;
            var accent = (m.Art != null ? AlbumColorExtractor.Extract(m.Art) : null) ?? DefaultAccent;
            ApplyAccent(accent, hasArt: m.Art != null && m.Active);
        }
        MediaArtFallback.Visibility = MediaArt.Source == null ? Visibility.Visible : Visibility.Collapsed;

        // Presence badge.
        if (m.Active)
        {
            var p = App.Presences.Resolve(m);
            PresenceText.Text = p.SourceName;
            PresenceDot.Fill = new SolidColorBrush(p.Accent);
        }
        UpdateViewHeader();

        // Visualizer lifecycle, including compact pill bars.
        if (m.Playing && s.ShowVisualizer) App.Visualizer.Start();
        else App.Visualizer.Stop();

        var showProgress = m.Active && m.Dur > 1;
        ProgressTrack.Visibility = showProgress ? Visibility.Visible : Visibility.Hidden;
        TimeRow.Visibility = showProgress ? Visibility.Visible : Visibility.Hidden;
        _lastShownSec = -1; // Refresh time labels on the next frame.

        RefreshPill();
    }

    /// <summary>Renders the pill's right side in priority order: temporary Discord flash,
    /// active Discord speaker, persistent channel badge, media bars/title, then idle dot.</summary>
    private void RefreshPill()
    {
        if (_pillFlashActive) return; // Do not replace an active flash.

        var discord = App.Discord.State;
        _discordSpeaker = discord.Members.FirstOrDefault(m => m.Speaking);

        // Local mic/headphone state on the left, shown only in a voice channel.
        var self = App.Discord.SelfMember;
        if (self is { Deafened: true })
        {
            PillMuteIcon.Visibility = Visibility.Visible;
            PillMuteIcon.ToolTip = App.Loc.T("discord.deafened");
        }
        else if (self is { Muted: true })
        {
            PillMuteIcon.Visibility = Visibility.Visible;
            PillMuteIcon.ToolTip = App.Loc.T("discord.muted");
        }
        else
        {
            PillMuteIcon.Visibility = Visibility.Collapsed;
        }

        var showSpeaker = _discordSpeaker != null;
        var showIdleChannel = !showSpeaker && discord.InChannel;
        var showBars = !showSpeaker && !showIdleChannel && _media.Active && App.Settings.Current.ShowVisualizer;
        var showDot = !showSpeaker && !showIdleChannel && !_media.Active;

        PillDiscordAvatar.Visibility = showSpeaker ? Visibility.Visible : Visibility.Collapsed;
        PillDiscordLogo.Visibility = showIdleChannel ? Visibility.Visible : Visibility.Collapsed;
        PillBarsPanel.Visibility = showBars ? Visibility.Visible : Visibility.Collapsed;
        PillDot.Visibility = showDot ? Visibility.Visible : Visibility.Collapsed;

        if (showSpeaker)
        {
            PillTitle.Visibility = Visibility.Visible;
            PillTitle.Text = _discordSpeaker!.Name;
            if (_discordSpeaker.AvatarUrl != null)
            {
                var cached = AvatarCache.TryGet(_discordSpeaker.AvatarUrl);
                if (cached != null) PillDiscordBrush.ImageSource = cached;
                else LoadPillAvatarAsync(_discordSpeaker.AvatarUrl);
            }
        }
        else if (showIdleChannel)
        {
            PillTitle.Visibility = Visibility.Visible;
            PillTitle.Text = discord.ChannelName;
        }
        else if (_media.Active)
        {
            PillTitle.Visibility = Visibility.Visible;
            PillTitle.Text = string.IsNullOrWhiteSpace(_media.Artist) ? _media.Title : $"{_media.Title} — {_media.Artist}";
        }
        else
        {
            PillTitle.Visibility = Visibility.Collapsed;
        }

        var wide = showSpeaker || showIdleChannel || _media.Active;
        if (wide != _pillWide)
        {
            _pillWide = wide;
            if (_state == IslandState.Pill) ApplyState(true);
        }
    }

    private async void LoadPillAvatarAsync(string url)
    {
        var img = await AvatarCache.LoadAsync(url);
        if (img != null && _discordSpeaker?.AvatarUrl == url)
            PillDiscordBrush.ImageSource = img;
    }

    /// <summary>Temporarily highlights the pill with an icon and text for about four
    /// seconds after a Discord join or notification, then restores its regular state.</summary>
    private void FlashPill(ImageSource? icon, string text)
    {
        _pillFlashActive = true;

        var hasIcon = icon != null;
        PillDiscordAvatar.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
        PillDiscordLogo.Visibility = hasIcon ? Visibility.Collapsed : Visibility.Visible;
        PillBarsPanel.Visibility = Visibility.Collapsed;
        PillDot.Visibility = Visibility.Collapsed;
        PillTitle.Visibility = Visibility.Visible;
        PillTitle.Text = text;
        if (hasIcon) PillDiscordBrush.ImageSource = icon;

        if (!_pillWide)
        {
            _pillWide = true;
            if (_state == IslandState.Pill) ApplyState(true);
        }

        _pillFlashTimer.Stop();
        _pillFlashTimer.Start();
    }

    private async void OnDiscordMemberJoined(DiscordMember m)
    {
        ImageSource? icon = m.AvatarUrl != null ? AvatarCache.TryGet(m.AvatarUrl) : null;
        if (icon == null && m.AvatarUrl != null) icon = await AvatarCache.LoadAsync(m.AvatarUrl);
        FlashPill(icon, string.Format(App.Loc.T("discord.joined"), m.Name));
    }

    private static BitmapImage? ToImage(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => App.Media.PlayPause();
    private void BtnNext_Click(object sender, RoutedEventArgs e) => App.Media.Next();
    private void BtnPrev_Click(object sender, RoutedEventArgs e) => App.Media.Prev();

    // ---------- Colour ----------

    /// <summary>Smoothly applies the album colour to bars, progress, and the top glow.</summary>
    private void ApplyAccent(Color c, bool hasArt)
    {
        var dur = TimeSpan.FromMilliseconds(600);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        _accentBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(c, dur) { EasingFunction = ease });

        var tint = Color.FromArgb(hasArt ? (byte)0x40 : (byte)0x00, c.R, c.G, c.B);
        TintStop.BeginAnimation(GradientStop.ColorProperty,
            new ColorAnimation(tint, dur) { EasingFunction = ease });
    }

    // ---------- Visualizer and timeline (frame-synchronised) ----------

    private void BuildBars()
    {
        ProgressFill.Background = _accentBrush;

        for (int i = 0; i < 4; i++)
        {
            var bar = new Border
            {
                Background = _accentBrush,
                Width = 3,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1.2, 0, 1.2, 0),
                Height = 3
            };
            _pillBars.Add(bar);
            PillBarsPanel.Children.Add(bar);
        }
    }

    private static readonly int[] PillPick = { 3, 8, 13, 17 };

    private void OnFrame()
    {
        var now = DateTime.Now;
        var dt = (float)Math.Clamp((now - _lastFrame).TotalSeconds, 0.001, 0.05);
        _lastFrame = now;

        var expVisible = ExpandedView.Visibility == Visibility.Visible;
        var pillVisible = PillView.Visibility == Visibility.Visible
                          && _media.Active && App.Settings.Current.ShowVisualizer;
        if (!expVisible && !pillVisible) return; // Avoid work while nothing is visible.

        // Timeline and duration, using SMTC LastUpdatedTime for live position.
        if (expVisible && _media.Active && _media.Dur > 1)
        {
            var pos = _media.Pos;
            if (_media.Playing) pos += (now - _media.PosAt).TotalSeconds;
            pos = Math.Clamp(pos, 0, _media.Dur);
            ProgressFill.Width = pos / _media.Dur * ProgressTrack.ActualWidth;

            var sec = (int)pos;
            if (sec != _lastShownSec) // Update text only when the displayed second changes.
            {
                _lastShownSec = sec;
                MediaPosText.Text = FormatTime(pos);
                MediaDurText.Text = FormatTime(_media.Dur);
            }
        }

        // Spectrum and compact pill bars. FFT runs at most at 30 Hz; display is smoothed per frame.
        if (!pillVisible) return;

        if ((now - _lastFft).TotalMilliseconds >= 33)
        {
            _lastFft = now;
            if (App.Visualizer.Running)
            {
                App.Visualizer.GetTargets(_targets);
            }
            else
            {
                for (int i = 0; i < _targets.Length; i++)
                    _targets[i] = _media.Playing ? (float)(_rng.NextDouble() * 0.55 + 0.08) : 0f;
            }
        }

        // Time-based smoothing: instant attack and roughly 0.25-second decay.
        var rise = Math.Min(1f, dt * 30f);
        var fall = Math.Min(1f, dt * 9f);
        for (int i = 0; i < _pillBars.Count; i++)
        {
            var b = PillPick[i];
            var t = _targets[b];
            _disp[b] = t > _disp[b]
                ? _disp[b] + (t - _disp[b]) * rise
                : _disp[b] - _disp[b] * fall;
            _pillBars[i].Height = 3 + _disp[b] * 13;
        }
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    // ---------- Calendar ----------

    private void BtnCalPrev_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); }
    private void BtnCalNext_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); }

    private void BuildCalendar()
    {
        var c = App.Loc.Culture;
        CalMonthLabel.Text = c.TextInfo.ToTitleCase(_calMonth.ToString("MMMM yyyy", c));

        CalDayNames.Children.Clear();
        var firstDow = (int)c.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var d = (DayOfWeek)((firstDow + i) % 7);
            CalDayNames.Children.Add(new TextBlock
            {
                Text = c.DateTimeFormat.GetShortestDayName(d),
                FontSize = 9,
                Opacity = 0.55,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        CalGrid.Children.Clear();
        var first = new DateTime(_calMonth.Year, _calMonth.Month, 1);
        var lead = (((int)first.DayOfWeek - firstDow) + 7) % 7;
        var start = first.AddDays(-lead);
        var today = DateTime.Today;

        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var tb = new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                Opacity = day.Month == _calMonth.Month ? 0.9 : 0.25
            };

            if (day == today)
            {
                CalGrid.Children.Add(new Border
                {
                    Background = _accentBrush,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(4, 0.5, 4, 0.5),
                    Child = new TextBlock
                    {
                        Text = day.Day.ToString(),
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.Black,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }
            else CalGrid.Children.Add(tb);
        }
    }

    // ---------- File Tray ----------

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (!App.Settings.Current.ShowViewTray) return; // Ignore drops while the File Tray tab is disabled.
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        if (_state != IslandState.Expanded) SetState(IslandState.Expanded);
        if (_viewIndex != 2) SwitchView(2);
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            App.FileTray.Add(paths);
    }

    private void RefreshTray()
    {
        TrayPanel.Children.Clear();
        var items = App.FileTray.Items;
        TrayEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayTipText.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in items)
            TrayPanel.Children.Add(BuildTrayTile(path));
    }

    private FrameworkElement BuildTrayTile(string path)
    {
        var panel = new StackPanel
        {
            Width = 62,
            Margin = new Thickness(4, 2, 4, 2),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = path
        };

        var img = new Image { Width = 26, Height = 26, Margin = new Thickness(0, 3, 0, 2) };
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                img.Source = src;
            }
        }
        catch { }
        panel.Children.Add(img);

        panel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 9,
            Foreground = Brushes.White,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 24,
            TextWrapping = TextWrapping.Wrap
        });

        Point dragStart = default;
        bool opened = false;

        panel.MouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(this);
            opened = false;
            if (e.ClickCount == 2)
            {
                opened = true;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            }
        };
        panel.MouseMove += (_, e) =>
        {
            if (opened || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - dragStart.X) + Math.Abs(p.Y - dragStart.Y) < 10) return;
            opened = true; // Prevent another trigger from the same gesture.
            try
            {
                DragDrop.DoDragDrop(panel,
                    new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
            }
            catch { }
        };
        panel.MouseRightButtonUp += (_, _) => App.FileTray.Remove(path);

        return panel;
    }

    // ---------- Bluetooth ----------

    private async Task RefreshBtAsync()
    {
        var devices = await BluetoothService.GetConnectedAsync();
        BtPanel.Children.Clear();
        BtEmptyText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var d in devices.Take(4))
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = d.Name,
                FontSize = 12.5,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 60, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = d.Battery.HasValue ? $"%{d.Battery}" : "—",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _accentBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            });
            BtPanel.Children.Add(row);
        }
    }

    // ---------- Discord ----------

    private void OnDiscordChanged()
    {
        RefreshPill();
        if (_viewIndex == 4) RefreshDiscordView();
    }

    private void RefreshDiscordView()
    {
        DiscordPanel.Children.Clear();
        var state = App.Discord.State;

        DiscordChannelHeader.Visibility = state.InChannel ? Visibility.Visible : Visibility.Collapsed;
        DiscordChannelName.Text = state.ChannelName;
        DiscordGuildName.Text = string.IsNullOrEmpty(state.GuildName) ? "" : $" • {state.GuildName}";

        DiscordEmptyText.Text = App.Loc.T("discord.empty");
        DiscordEmptyText.Visibility = (state.InChannel && state.Members.Count > 0)
            ? Visibility.Collapsed : Visibility.Visible;

        var shown = state.Members.Take(DiscordMaxShown).ToList();
        foreach (var m in shown)
            DiscordPanel.Children.Add(BuildDiscordTile(m));

        var overflow = state.Members.Count - shown.Count;
        if (overflow > 0)
            DiscordPanel.Children.Add(BuildDiscordOverflowTile(overflow));
    }

    private FrameworkElement BuildDiscordTile(DiscordMember m)
    {
        var panel = new StackPanel { Width = 58, Margin = new Thickness(4), ToolTip = m.Name };

        var ringColor = m.Speaking ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        var fallback = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5E)),
            Child = new TextBlock
            {
                Text = m.Name.Length > 0 ? m.Name[..1].ToUpperInvariant() : "?",
                FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };

        var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
        var ellipse = new Ellipse { Width = 38, Height = 38, Fill = brush };

        var inner = new Grid { Width = 38, Height = 38 };
        inner.Children.Add(fallback);
        inner.Children.Add(ellipse);

        var avatarBorder = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
            BorderBrush = new SolidColorBrush(ringColor),
            BorderThickness = new Thickness(m.Speaking ? 2.5 : 1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = inner
        };

        if (m.AvatarUrl != null)
        {
            var cached = AvatarCache.TryGet(m.AvatarUrl);
            if (cached != null) { brush.ImageSource = cached; fallback.Visibility = Visibility.Collapsed; }
            else LoadDiscordTileAvatarAsync(m.AvatarUrl, brush, fallback);
        }

        panel.Children.Add(avatarBorder);
        panel.Children.Add(new TextBlock
        {
            Text = m.Name,
            FontSize = 9.5,
            Foreground = Brushes.White,
            Opacity = m.Muted || m.Deafened ? 0.5 : 0.85,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        });

        return panel;
    }

    private async void LoadDiscordTileAvatarAsync(string url, ImageBrush brush, Border fallback)
    {
        var img = await AvatarCache.LoadAsync(url);
        if (img == null) return;
        brush.ImageSource = img;
        fallback.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement BuildDiscordOverflowTile(int count)
    {
        var panel = new StackPanel { Width = 58, Margin = new Thickness(4) };
        panel.Children.Add(new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "+" + count, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        });
        panel.Children.Add(new TextBlock
        {
            Text = App.Loc.T("discord.more"),
            FontSize = 9.5, Foreground = Brushes.White, Opacity = 0.7,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
        });
        return panel;
    }

    // ---------- Notifications ----------

    private void ShowToast(ToastInfo t)
    {
        AppLog.Write($"Bildirim alındı: App='{t.App}' Title='{t.Title}'");
        if (!App.Settings.Current.ShowNotifications) return;

        // Keep Discord mentions/DMs out of the large card and show a short pill flash instead.
        // Contains also matches variants such as Discord PTB and Canary.
        if (t.App.Contains("discord", StringComparison.OrdinalIgnoreCase))
        {
            var icon = AppIconResolver.Resolve("Discord");
            var text = !string.IsNullOrWhiteSpace(t.Title) ? t.Title : t.Body;
            if (!string.IsNullOrWhiteSpace(text)) FlashPill(icon, text);
            return;
        }

        NotifAppName.Text = t.App.ToUpper(App.Loc.Culture);
        NotifTime.Text = App.Loc.T("notif.now");
        NotifTitle.Text = t.Title;
        NotifBody.Text = t.Body;
        NotifAppInitial.Text = t.App.Length > 0 ? t.App[..1].ToUpperInvariant() : "!";

        _notifTimer.Stop();
        if (_state != IslandState.Notif) SetState(IslandState.Notif);
        _notifTimer.Start();
    }

    // ---------- Settings and language ----------

    private void OnSettingsChanged()
    {
        OnMedia(_media);
        ApplyState(false);
        UpdateClock();
        ApplyTexts();
        BuildDots();
        if (!EnabledViews().Contains(_viewIndex)) SwitchView(0, animate: false);
    }

    private void ApplyTexts()
    {
        TrayEmptyText.Text = App.Loc.T("filetray.empty");
        TrayTipText.Text = App.Loc.T("filetray.tip");
        BtEmptyText.Text = App.Loc.T("bt.none");
        UpdateViewHeader();
        BuildCalendar();
        UpdateClock();
        if (_viewIndex == 4) RefreshDiscordView();
    }

    private void StartDotPulse()
    {
        var pulse = new DoubleAnimation(0.45, 1.0, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        PillDot.BeginAnimation(OpacityProperty, pulse);
    }
}
