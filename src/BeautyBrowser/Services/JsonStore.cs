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
        try
        {
            var path = Path.Combine(Folder, fileName);
            if (!File.Exists(path)) return new T();
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch
        {
            return new T(); // corrupt or from another version: start over
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
