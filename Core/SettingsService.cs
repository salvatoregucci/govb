using System.IO;
using System.Text.Json;

namespace DynamicIsland.Core;

public class AppSettings
{
    public string Language { get; set; } = "tr";
    public string Theme { get; set; } = "floating"; // floating | attached | glass
    public bool ShowVisualizer { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool ShowMediaIcon { get; set; } = true;
    public bool ShowBattery { get; set; } = true;

    // Tabs in the expanded panel (Media is always enabled).
    public bool ShowViewCalendar { get; set; } = true;
    public bool ShowViewTray { get; set; } = true;
    public bool ShowViewBt { get; set; } = true;
    public bool ShowViewDiscord { get; set; } = true;

    // Credentials for the app created in the Discord Developer Portal.
    public string DiscordClientId { get; set; } = "";
    public string DiscordClientSecret { get; set; } = "";
}

public class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public SettingsService()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { Current = new AppSettings(); }
        Current.Language = SupportedLanguages.Normalize(Current.Language);
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Current.Language = SupportedLanguages.Normalize(Current.Language);
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* disk hatası ada'yı düşürmesin */ }
    }
}
