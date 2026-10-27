using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyManager.Models;

namespace ProxyManager.Services;

public sealed class SettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProxyManager");

    public string Directory { get; } = directory;

    private string FilePath => Path.Combine(Directory, "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Keep the broken file for manual recovery instead of silently overwriting it.
            File.Copy(FilePath, FilePath + ".broken", overwrite: true);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
