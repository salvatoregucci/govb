using System.Windows;

namespace DynamicIsland.Core;

public class ThemeService
{
    private ResourceDictionary? _active;
    private readonly SettingsService _settings;
    private string _applied = "";

    public event Action? Changed;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        Apply(settings.Current.Theme);
        settings.Changed += () => Apply(settings.Current.Theme);
    }

    private void Apply(string theme)
    {
        if (theme == _applied) return;
        _applied = theme;

        var name = theme switch
        {
            "attached" => "Attached",
            "glass" => "Glass",
            _ => "Floating"
        };

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{name}.xaml")
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_active != null) merged.Remove(_active);
        merged.Add(dict);
        _active = dict;
        Changed?.Invoke();
    }

    /// <summary>Whether the Attached theme docks the island to the top screen edge.</summary>
    public bool AttachTop =>
        Application.Current.TryFindResource("AttachTop") is bool b && b;
}
