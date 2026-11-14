using System.IO;
using System.Globalization;
using System.Text.Json;

namespace DynamicIsland.Core;

public class LocalizationService
{
    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallbackStrings = new();
    private readonly SettingsService _settings;

    public event Action? Changed;

    public LocalizationService(SettingsService settings)
    {
        _settings = settings;
        LoadFallback();
        Load(settings.Current.Language);
        settings.Changed += () => Load(settings.Current.Language);
    }

    private string _loaded = "";

    private void LoadFallback()
    {
        _fallbackStrings = ReadLanguageFile("en");
    }

    private static Dictionary<string, string> ReadLanguageFile(string lang)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Languages", lang + ".json");
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void Load(string lang)
    {
        lang = SupportedLanguages.Normalize(lang);
        if (lang == _loaded) return;
        _loaded = lang;
        _strings = ReadLanguageFile(lang);
        Changed?.Invoke();
    }

    /// <summary>Returns a localized string; the key itself is shown when no value exists.</summary>
    public string T(string key) => _strings.TryGetValue(key, out var value)
        ? value
        : _fallbackStrings.TryGetValue(key, out value) ? value : key;

    public CultureInfo Culture
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(SupportedLanguages.Normalize(_settings.Current.Language)); }
            catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en-US"); }
        }
    }
}

public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>Languages shipped with the application. English remains the safe fallback
/// for a string that has not yet been translated in an optional language file.</summary>
public static class SupportedLanguages
{
    public static readonly IReadOnlyList<LanguageOption> All = new[]
    {
        new LanguageOption("ar", "العربية"), new LanguageOption("bg", "Български"),
        new LanguageOption("cs", "Čeština"), new LanguageOption("da", "Dansk"),
        new LanguageOption("de", "Deutsch"), new LanguageOption("el", "Ελληνικά"),
        new LanguageOption("en", "English"), new LanguageOption("es", "Español"),
        new LanguageOption("fi", "Suomi"), new LanguageOption("fr", "Français"),
        new LanguageOption("he", "עברית"), new LanguageOption("hi", "हिन्दी"),
        new LanguageOption("hu", "Magyar"), new LanguageOption("id", "Bahasa Indonesia"),
        new LanguageOption("it", "Italiano"), new LanguageOption("ja", "日本語"),
        new LanguageOption("ko", "한국어"), new LanguageOption("ms", "Bahasa Melayu"),
        new LanguageOption("nb", "Norsk bokmål"), new LanguageOption("nl", "Nederlands"),
        new LanguageOption("pl", "Polski"), new LanguageOption("pt-BR", "Português (Brasil)"),
        new LanguageOption("ro", "Română"), new LanguageOption("ru", "Русский"),
        new LanguageOption("sk", "Slovenčina"), new LanguageOption("sv", "Svenska"),
        new LanguageOption("th", "ไทย"), new LanguageOption("tr", "Türkçe"),
        new LanguageOption("uk", "Українська"), new LanguageOption("vi", "Tiếng Việt"),
        new LanguageOption("zh-Hans", "简体中文"), new LanguageOption("zh-Hant", "繁體中文")
    };

    public static string Normalize(string? language) => All.Any(x => x.Code.Equals(language, StringComparison.OrdinalIgnoreCase))
        ? All.First(x => x.Code.Equals(language, StringComparison.OrdinalIgnoreCase)).Code
        : "en";
}
