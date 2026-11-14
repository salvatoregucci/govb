using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.UI;

public partial class SettingsWindow : Window
{
    private readonly DispatcherTimer _ramTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<RadioButton> _themeOptions = new();
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        LanguagePicker.ItemsSource = SupportedLanguages.All;
        BuildThemeOptions();
        LoadFromSettings();
        ApplyTexts();
        App.Loc.Changed += ApplyTexts;

        _ramTimer.Tick += (_, _) => UpdateRam();
        _ramTimer.Start();
        UpdateRam();

        // Discord RPC events may arrive on a background thread; marshal them to the UI.
        Action discordStatusHandler = () => Dispatcher.Invoke(UpdateDiscordStatus);
        App.Discord.StatusChanged += discordStatusHandler;
        UpdateDiscordStatus();

        Closed += (_, _) =>
        {
            _ramTimer.Stop();
            App.Loc.Changed -= ApplyTexts;
            App.Discord.StatusChanged -= discordStatusHandler;
        };
    }

    private void BuildThemeOptions()
    {
        foreach (var key in new[] { "floating", "attached", "glass" })
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("ThemeOption"),
                GroupName = "theme",
                Tag = key,
                Content = BuildThemeContent(key)
            };
            rb.Checked += (_, _) =>
            {
                if (_loading) return;
                App.Settings.Update(s => s.Theme = key);
            };
            _themeOptions.Add(rb);
            ThemePanel.Children.Add(rb);
        }
    }

    /// <summary>Small preview containing the theme's pill shape and name.</summary>
    private static UIElement BuildThemeContent(string key)
    {
        var panel = new StackPanel { Width = 88 };

        var preview = new Border
        {
            Height = 34,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var pill = new Border
        {
            Width = 44,
            Height = 12,
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, key == "attached" ? 0 : 5, 0, 0),
            CornerRadius = key == "attached" ? new CornerRadius(0, 0, 6, 6) : new CornerRadius(6)
        };
        if (key == "glass")
        {
            pill.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x18, 0x18, 0x1C));
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            pill.BorderThickness = new Thickness(1);
        }
        preview.Child = pill;
        panel.Children.Add(preview);

        var label = new TextBlock
        {
            FontSize = 11,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Tag = "themelabel:" + key
        };
        panel.Children.Add(label);
        return panel;
    }

    private void LoadFromSettings()
    {
        var s = App.Settings.Current;

        LanguagePicker.SelectedValue = SupportedLanguages.Normalize(s.Language);

        foreach (var rb in _themeOptions)
            rb.IsChecked = (string)rb.Tag == s.Theme;

        ChkVisualizer.IsChecked = s.ShowVisualizer;
        ChkNotifications.IsChecked = s.ShowNotifications;
        ChkMediaIcon.IsChecked = s.ShowMediaIcon;
        ChkBattery.IsChecked = s.ShowBattery;
        ChkStartup.IsChecked = StartupService.IsEnabled;
        ChkViewCal.IsChecked = s.ShowViewCalendar;
        ChkViewTray.IsChecked = s.ShowViewTray;
        ChkViewBt.IsChecked = s.ShowViewBt;
        ChkViewDiscord.IsChecked = s.ShowViewDiscord;

        TxtDiscordClientId.Text = s.DiscordClientId;
        TxtDiscordClientSecret.Password = s.DiscordClientSecret;

        _loading = false;
    }

    private void ApplyTexts()
    {
        var L = App.Loc;
        var c = L.Culture; // Culture-aware casing preserves characters such as Turkish dotted I.
        HeaderTitle.Text = L.T("settings.title");
        Title = L.T("settings.title");
        LblLanguage.Text = L.T("settings.language").ToUpper(c);
        LblLanguageRow.Text = L.T("settings.language");
        LblTheme.Text = L.T("settings.theme").ToUpper(c);
        LblModules.Text = L.T("settings.modules").ToUpper(c);
        LblVisualizer.Text = L.T("settings.visualizer");
        LblNotifications.Text = L.T("settings.notifications");
        LblNotifHint.Text = L.T("settings.notif.permission");
        LblMediaIcon.Text = L.T("settings.mediaicon");
        LblBattery.Text = L.T("settings.battery");
        LblStartup.Text = L.T("settings.startup");
        LblViews.Text = L.T("settings.views").ToUpper(c);
        LblViewCal.Text = L.T("view.calendar");
        LblViewTray.Text = L.T("view.tray");
        LblViewBt.Text = L.T("view.bt");
        LblViewDiscord.Text = L.T("view.discord");
        LblAbout.Text = L.T("settings.about").ToUpper(c);
        LblRam.Text = L.T("settings.ram");
        LblVersion.Text = L.T("settings.version");

        LblDiscordSection.Text = L.T("settings.discord").ToUpper(c);
        LblDiscordClientId.Text = L.T("settings.discord.clientid");
        LblDiscordClientSecret.Text = L.T("settings.discord.clientsecret");
        LblDiscordHint.Text = L.T("settings.discord.hint");
        BtnDiscordRetry.Content = L.T("settings.discord.retry");
        BtnDiscordSave.Content = L.T("settings.discord.save");
        UpdateDiscordStatus();

        var themeNames = new Dictionary<string, string>
        {
            ["floating"] = L.T("settings.theme.floating"),
            ["attached"] = L.T("settings.theme.attached"),
            ["glass"] = L.T("settings.theme.glass")
        };
        foreach (var rb in _themeOptions)
            if (rb.Content is StackPanel sp)
                foreach (var child in sp.Children)
                    if (child is TextBlock tb && tb.Tag is string tag && tag.StartsWith("themelabel:"))
                        tb.Text = themeNames[tag["themelabel:".Length..]];
    }

    private void UpdateRam()
    {
        using var p = Process.GetCurrentProcess();
        TxtRam.Text = $"{p.WorkingSet64 / (1024.0 * 1024.0):F0} MB";
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguagePicker.SelectedValue is not string language) return;
        App.Settings.Update(s => s.Language = language);
    }

    private void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (ReferenceEquals(sender, ChkStartup))
            StartupService.SetEnabled(ChkStartup.IsChecked == true);

        App.Settings.Update(s =>
        {
            s.ShowVisualizer = ChkVisualizer.IsChecked == true;
            s.ShowNotifications = ChkNotifications.IsChecked == true;
            s.ShowMediaIcon = ChkMediaIcon.IsChecked == true;
            s.ShowBattery = ChkBattery.IsChecked == true;
            s.ShowViewCalendar = ChkViewCal.IsChecked == true;
            s.ShowViewTray = ChkViewTray.IsChecked == true;
            s.ShowViewBt = ChkViewBt.IsChecked == true;
            s.ShowViewDiscord = ChkViewDiscord.IsChecked == true;
        });
    }

    private async void BtnDiscordSave_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Update(s =>
        {
            s.DiscordClientId = TxtDiscordClientId.Text.Trim();
            s.DiscordClientSecret = TxtDiscordClientSecret.Password.Trim();
        });
        await App.Discord.RetryAsync();
    }

    private async void BtnDiscordRetry_Click(object sender, RoutedEventArgs e) => await App.Discord.RetryAsync();

    private void UpdateDiscordStatus()
    {
        var d = App.Discord;
        var L = App.Loc;
        if (d.IsAuthenticated)
        {
            DiscordStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            LblDiscordStatus.Text = string.Format(L.T("settings.discord.status.connected"), d.AuthenticatedUserName ?? "?");
        }
        else
        {
            DiscordStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            LblDiscordStatus.Text = L.T("settings.discord.status.disconnected");
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton)
            try { DragMove(); } catch { }
    }
}
