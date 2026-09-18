using System.Text.Json;

namespace RecordeRy.Core.Settings;

public static class SettingsStore
{
    public static string GetSettingsPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "RecordeRy");

        Directory.CreateDirectory(directory);

        return Path.Combine(directory, "settings.json");
    }

    public static AppSettings Load()
    {
        var path = GetSettingsPath();

        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(path);

            return JsonSerializer.Deserialize<AppSettings>(json)
                ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var path = GetSettingsPath();

        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json);
    }
}
