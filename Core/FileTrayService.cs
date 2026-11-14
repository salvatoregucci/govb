using System.IO;
using System.Text.Json;

namespace DynamicIsland.Core;

/// <summary>DynamicWin-style File Tray. Paths dropped on the island are persisted,
/// can be opened with a double-click, and can be dragged back out.</summary>
public class FileTrayService
{
    private const int MaxItems = 12;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "tray.json");

    public List<string> Items { get; private set; } = new();
    public event Action? Changed;

    public FileTrayService()
    {
        try
        {
            if (File.Exists(FilePath))
                Items = (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new())
                    .Where(File.Exists).ToList();
        }
        catch { Items = new(); }
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Items.Count >= MaxItems) break;
            if (!Items.Contains(p, StringComparer.OrdinalIgnoreCase) && (File.Exists(p) || Directory.Exists(p)))
                Items.Add(p);
        }
        Save();
        Changed?.Invoke();
    }

    public void Remove(string path)
    {
        Items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items));
        }
        catch { }
    }
}
