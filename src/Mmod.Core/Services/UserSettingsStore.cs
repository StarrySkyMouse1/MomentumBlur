using System.Text.Json;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _settingsPath;

    public UserSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProjectConstants.AppDataFolderName);
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, ProjectConstants.SettingsFileName);
    }

    public UserSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new UserSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
            SettingsMigration.Normalize(settings);
            return settings;
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
