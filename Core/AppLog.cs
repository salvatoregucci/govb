using System.IO;

namespace DynamicIsland.Core;

/// <summary>Lightweight diagnostic log for handled errors and application events.
/// Stored at `%APPDATA%\DynamicIsland\app.log`.</summary>
public static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "app.log");
    private static readonly object Lock = new();

    public static void Write(string line)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
                if (new FileInfo(LogPath).Length > 200_000)
                {
                    var lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines.Skip(Math.Max(0, lines.Length - 500)));
                }
            }
            catch { }
        }
    }

    public static void WriteException(string source, Exception exception) =>
        Write($"{source}: {exception}");
}
