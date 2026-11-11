using System.Text.Json;

namespace BeautyBrowser.Services;

/// <summary>Reads and writes JSON files in %LOCALAPPDATA%\BeautyBrowser.</summary>
internal static class JsonStore
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyBrowser");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string fileName) where T : new()
    {
        var path = Path.Combine(Folder, fileName);
        string text;
        try
        {
            if (!File.Exists(path)) return new T();
            text = File.ReadAllText(path);
        }
        catch
        {
            return new T(); // unreadable right now (locked, permissions): the next Save may still overwrite it
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Options) ?? new T();
        }
        catch
        {
            // Corrupt or from another version: keep it aside so the next Save doesn't destroy the user's data.
            try { File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
            catch { /* best effort */ }
            return new T();
        }
    }

    public static void Save<T>(string fileName, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, fileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true); // atomic write
        }
        catch
        {
            // Failing to save must never break browsing.
        }
    }
}
