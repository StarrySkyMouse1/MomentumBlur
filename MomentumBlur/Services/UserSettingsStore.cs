using System.IO;
using System.Text.Json;
using mmod_record.Models;

namespace mmod_record.Services;

public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>绋嬪簭杩愯鐩綍锛坋xe 鎵€鍦ㄧ洰褰曪級銆?/summary>
    public static string SettingsDirectory => ResolveAppBaseDirectory();

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    public static bool LastLoadResetInvalidPaths { get; private set; }

    public static UserSettings Load()
    {
        LastLoadResetInvalidPaths = false;
        try
        {
            TryMigrateLegacySettingsFile();
            if (!File.Exists(SettingsFilePath))
                return new UserSettings();

            var json = File.ReadAllText(SettingsFilePath);
            var settings = DeserializeWithLegacyFields(json);
            Normalize(settings);
            if (ResetInvalidPathValues(settings))
            {
                LastLoadResetInvalidPaths = true;
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    public static void Normalize(UserSettings settings)
    {
        settings.ObsCaptureFramerate = ObsOutputPathHelper.NormalizeObsCaptureFramerate(settings.ObsCaptureFramerate);
        settings.SupersamplingMultiplier = Math.Clamp(settings.SupersamplingMultiplier, 1, 120);
        settings.CompositionBackend = CompositionBackendCatalog.Normalize(settings.CompositionBackend);
        settings.VideoEncoder = VideoEncoderCatalog.Normalize(settings.VideoEncoder);
        settings.Crf = settings.Crf is > 0 and <= 51 ? settings.Crf : 18;
        settings.Exposure = settings.Exposure is > 0 and <= 1 ? settings.Exposure : 0.5;

        settings.MaxParallelGpuJobs = Math.Clamp(settings.MaxParallelGpuJobs, 1, 4);
        GpuSynthesisConcurrency.Configure(settings.MaxParallelGpuJobs);
    }

    public static bool ResetInvalidPathValues(UserSettings settings)
    {
        var reset = false;

        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath) &&
            !TryFileExists(settings.FfmpegPath))
        {
            settings.FfmpegPath = null;
            reset = true;
        }

        if (!string.IsNullOrWhiteSpace(settings.VideoOutputDirectory) &&
            !HasExistingRoot(settings.VideoOutputDirectory))
        {
            settings.VideoOutputDirectory = null;
            reset = true;
        }

        return reset;
    }

    private static void TryMigrateLegacySettingsFile()
    {
        if (File.Exists(SettingsFilePath))
            return;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var legacyPath in EnumerateLegacySettingsPaths(appData))
        {
            if (!File.Exists(legacyPath))
                continue;

            Directory.CreateDirectory(SettingsDirectory);
            File.Copy(legacyPath, SettingsFilePath, overwrite: false);
            PipelineLogger.Info($"Migrated settings.json from {legacyPath} to runtime directory.");
            return;
        }
    }

    private static IEnumerable<string> EnumerateLegacySettingsPaths(string appData)
    {
        yield return Path.Combine(appData, "mmod_record_obs", "settings.json");
        yield return Path.Combine(appData, "mmod_record", "settings.json");

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "mmod_record_obs", "bin", "Debug", "net10.0-windows", "settings.json");
            yield return Path.Combine(current.FullName, "mmod_record_obs", "bin", "Release", "net10.0-windows", "settings.json");
            current = current.Parent;
        }
    }

    private static string ResolveAppBaseDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static UserSettings DeserializeWithLegacyFields(string json)
    {
        var settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!ProjectConstants.SupportedObsCaptureFramerates.Contains(settings.ObsCaptureFramerate) &&
                TryReadInt(root, "supersamplingMultiplier", out var n) &&
                n > 0)
            {
                settings.ObsCaptureFramerate = n >= 2 ? 120 : 60;
            }
        }
        catch
        {
            // 蹇界暐鏃?JSON 瑙ｆ瀽杈呭姪澶辫触
        }

        return settings;
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;

        value = prop.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out value) => true,
            JsonValueKind.String when int.TryParse(prop.GetString(), out value) => true,
            _ => false,
        };
    }

    private static bool TryFileExists(string? path)
    {
        try
        {
            var clean = PathSanitizer.Clean(path);
            return !string.IsNullOrWhiteSpace(clean) && File.Exists(Path.GetFullPath(clean.Trim('"')));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDirectoryExists(string? path)
    {
        try
        {
            var clean = PathSanitizer.Clean(path);
            return !string.IsNullOrWhiteSpace(clean) && Directory.Exists(Path.GetFullPath(clean));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExistingRoot(string? path)
    {
        try
        {
            var clean = PathSanitizer.Clean(path);
            if (string.IsNullOrWhiteSpace(clean))
                return false;

            var root = Path.GetPathRoot(Path.GetFullPath(clean));
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }
        catch
        {
            return false;
        }
    }
}

