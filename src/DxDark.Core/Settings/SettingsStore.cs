using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DxDark.Core.Profiles;

namespace DxDark.Core.Settings;

/// <summary>
/// Keeps the settings in %APPDATA%\DX Dark\DXDark.ini. Changes are written shortly after they
/// happen, and edits made to the file by hand are picked up while DX Dark runs. Settings from
/// earlier versions (settings.json) are converted once and kept as a backup.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const string FileName = "DXDark.ini";

    private const string LegacyFileName = "settings.json";

    /// <summary>For settings.json from versions 0.0.1–0.0.2 and presets exported by them.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _fileLock = new();
    private readonly Timer _saveTimer;
    private readonly Timer _reloadTimer;
    private FileSystemWatcher? _watcher;
    private string? _knownText;

    public SettingsStore(string directory)
    {
        Directory = directory;
        FilePath = Path.Combine(directory, FileName);
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _reloadTimer = new Timer(_ => CheckFileOnDisk(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DX Dark");

    public static string Version { get; } =
        typeof(SettingsStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string Directory { get; }

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    /// <summary>True when DX Dark had no settings yet (first run).</summary>
    public bool IsNew { get; private set; }

    /// <summary>
    /// The settings file was edited outside DX Dark. Raised on a background thread with the new
    /// settings; call <see cref="Replace"/> to use them.
    /// </summary>
    public event Action<AppSettings>? ChangedOnDisk;

    public void Load()
    {
        System.IO.Directory.CreateDirectory(Directory);
        string legacy = Path.Combine(Directory, LegacyFileName);
        if (File.Exists(FilePath))
        {
            string text = ReadText(FilePath) ?? "";
            AppSettings? settings = SettingsIni.Read(text);
            if (settings is null && text.Trim().Length > 0)
            {
                Log.Warn($"{FileName} could not be read; starting from defaults (the old file is kept as DXDark.bad.ini)");
                TryCopy(FilePath, Path.Combine(Directory, "DXDark.bad.ini"));
            }

            Current = settings ?? Defaults();
            lock (_fileLock)
            {
                _knownText = text;
            }

            // A settings.json next to DXDark.ini is stale (written by an older version); park it so it
            // can never bring old settings back.
            RetireLegacy(legacy);
        }
        else if (File.Exists(legacy))
        {
            Current = LoadLegacy(legacy);
            SaveNow();
            RetireLegacy(legacy);
        }
        else
        {
            IsNew = true;
            Current = Defaults();
            SaveNow();
        }
    }

    /// <summary>Watches the file for edits made outside DX Dark (see <see cref="ChangedOnDisk"/>).</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new FileSystemWatcher(Directory, FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => _reloadTimer.Change(400, Timeout.Infinite);
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.Error += (_, _) => _reloadTimer.Change(400, Timeout.Infinite); // e.g. the folder was deleted
        _watcher.Renamed += (_, e) =>
        {
            if (string.Equals(e.Name, FileName, StringComparison.OrdinalIgnoreCase))
            {
                _reloadTimer.Change(400, Timeout.Infinite);
            }
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Switches to settings read back from the file. Call on the thread that owns the settings.</summary>
    public void Replace(AppSettings settings) => Current = settings;

    /// <summary>Saves shortly after the last change, so dragging a slider writes the file once.</summary>
    public void SaveSoon() => _saveTimer.Change(500, Timeout.Infinite);

    public void SaveNow()
    {
        lock (_fileLock)
        {
            try
            {
                string text = SettingsIni.Write(Current, Version);
                if (text == _knownText && File.Exists(FilePath))
                {
                    return;
                }

                _knownText = text;
                System.IO.Directory.CreateDirectory(Directory);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (InvalidOperationException)
            {
                // A collection changed while writing; try again shortly.
                SaveSoon();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not save settings", ex);
            }
        }
    }

    /// <summary>Reads a preset file: DX Dark's .ini presets, or .json presets from versions 0.0.1–0.0.2.</summary>
    public static Profile? ImportProfile(string path)
    {
        string text = File.ReadAllText(path);
        if (text.TrimStart().StartsWith('{'))
        {
            Profile? profile = JsonSerializer.Deserialize<Profile>(text, JsonOptions);
            profile?.Normalize();
            return profile;
        }

        return SettingsIni.ReadPreset(text);
    }

    public static void ExportProfile(Profile profile, string path) =>
        File.WriteAllText(path, SettingsIni.WritePreset(profile));

    private void CheckFileOnDisk()
    {
        if (!File.Exists(FilePath))
        {
            // Deleted while DX Dark runs: start over from a fresh setup (no zones), as on a first run.
            lock (_fileLock)
            {
                if (_knownText is null)
                {
                    return;
                }

                _knownText = null;
            }

            Log.Info($"{FileName} was deleted; starting from a fresh setup");
            ChangedOnDisk?.Invoke(Defaults());
            return;
        }

        string? text = ReadText(FilePath);
        if (text is null)
        {
            return;
        }

        lock (_fileLock)
        {
            if (text == _knownText)
            {
                return; // our own save, or no real change
            }
        }

        AppSettings? settings = SettingsIni.Read(text);
        if (settings is null)
        {
            return; // empty or half-written: wait for the next save
        }

        lock (_fileLock)
        {
            _knownText = text;
        }

        Log.Info($"{FileName} was edited; reloading it");
        ChangedOnDisk?.Invoke(settings);
    }

    private void RetireLegacy(string legacy)
    {
        if (!File.Exists(legacy))
        {
            return;
        }

        string backup = UniquePath(Path.Combine(Directory, "settings.0.0.2-backup.json"));
        try
        {
            File.Move(legacy, backup);
            Log.Info($"The old {LegacyFileName} was kept as {Path.GetFileName(backup)}; settings live in {FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not rename {LegacyFileName}: {ex.Message}");
        }
    }

    private static AppSettings Defaults()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    private AppSettings LoadLegacy(string path)
    {
        try
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            Log.Error($"{LegacyFileName} could not be read; starting from defaults (the old file is kept as settings.bad.json)", ex);
            TryCopy(path, Path.Combine(Directory, "settings.bad.json"));
            return Defaults();
        }
    }

    /// <summary>The file's text, retrying briefly while an editor still has it open; null if unreadable.</summary>
    private static string? ReadText(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                Thread.Sleep(60);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private static void TryCopy(string from, string to)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string UniquePath(string path)
    {
        string candidate = path;
        for (int i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
        }

        return candidate;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _saveTimer.Dispose();
        _reloadTimer.Dispose();
    }
}
