using System.Windows;
using DynamicIsland.Core;
using DynamicIsland.Presence;
using DynamicIsland.UI;

namespace DynamicIsland;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWin;
    private bool _servicesStopped;

    public static SettingsService Settings { get; private set; } = null!;
    public static LocalizationService Loc { get; private set; } = null!;
    public static ThemeService Theme { get; private set; } = null!;
    public static MediaService Media { get; private set; } = null!;
    public static AudioVisualizerService Visualizer { get; private set; } = null!;
    public static NotificationService Notifications { get; private set; } = null!;
    public static PresenceManager Presences { get; private set; } = null!;
    public static FileTrayService FileTray { get; private set; } = null!;
    public static DiscordVoiceService Discord { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.WriteException("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exception)
                AppLog.WriteException("Unhandled process exception", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLog.WriteException("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        _mutex = new Mutex(true, "DynamicIslandWin_SingleInstance", out var isNew);
        if (!isNew) { Shutdown(); return; }

        Settings = new SettingsService();
        Loc = new LocalizationService(Settings);
        Theme = new ThemeService(Settings);
        Media = new MediaService();
        Visualizer = new AudioVisualizerService();
        Notifications = new NotificationService();
        Presences = new PresenceManager();
        FileTray = new FileTrayService();
        Discord = new DiscordVoiceService(Settings);

        var island = new IslandWindow();
        island.Show();

        _tray = new TrayIcon(OpenSettings, ExitApp);

        MemoryTrimmer.Start();
        if (e.Args.Contains("--settings")) OpenSettings();

        await Media.InitAsync();
        if (Settings.Current.ShowNotifications)
            _ = Notifications.InitAsync();
        _ = Discord.InitAsync(); // Remains inactive when no Client ID or Secret is configured.
    }

    private void OpenSettings()
    {
        if (_settingsWin is { IsLoaded: true })
        {
            _settingsWin.Activate();
            return;
        }
        _settingsWin = new SettingsWindow();
        _settingsWin.Show();
        _settingsWin.Activate();
    }

    private void ExitApp()
    {
        StopServices();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopServices();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void StopServices()
    {
        if (_servicesStopped) return;
        _servicesStopped = true;
        MemoryTrimmer.Stop();
        Visualizer?.Dispose();
        Media?.Dispose();
        Notifications?.Dispose();
        Discord?.Dispose();
        _tray?.Dispose();
    }
}
