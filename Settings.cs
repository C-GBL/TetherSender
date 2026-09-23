using System.Text.Json;

namespace TetherSender;

/// <summary>Persisted user choices (%APPDATA%\TetherSender\settings.json).</summary>
public sealed class Settings
{
    public string? AudioSourceId { get; set; }
    public string? DeviceSerial { get; set; }
    public int? PrebufferMs { get; set; }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TetherSender", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
