using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// %LOCALAPPDATA%\DeadlockScout\settings.json as an <see cref="IOptionsMonitor{ScoutSettings}"/>. Writes the defaults on
/// first run and reloads when the file changes, so edits apply without a restart. A malformed file keeps the last good
/// settings and is reported through <see cref="LastError"/>.
/// </summary>
public sealed class JsonSettingsMonitor : IOptionsMonitor<ScoutSettings>, IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly FileSystemWatcher? _watcher;
    private readonly List<Action<ScoutSettings, string?>> _listeners = new();
    private readonly object _gate = new();
    private ScoutSettings _current;

    public JsonSettingsMonitor(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (!File.Exists(path))
        {
            _current = ScoutSettings.Migrate(new ScoutSettings());
            File.WriteAllText(path, JsonSerializer.Serialize(_current, Json));
        }
        else
        {
            var loaded = Load();
            _current = loaded is null ? ScoutSettings.Migrate(new ScoutSettings()) : ScoutSettings.Migrate(loaded);
            if (loaded is not null && loaded.Version != _current.Version)
            {
                try { File.WriteAllText(path, JsonSerializer.Serialize(_current, Json)); } // once, so later hand edits stick
                catch (IOException ex) { LastError = ex.Message; }
            }
        }
        try
        {
            _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public static string DefaultPath()
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "settings.json");

    public string Path { get; }
    public string? LastError { get; private set; }

    public ScoutSettings CurrentValue { get { lock (_gate) return _current; } }
    public ScoutSettings Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<ScoutSettings, string?> listener)
    {
        lock (_gate) _listeners.Add(listener);
        return new Unsubscribe(() => { lock (_gate) _listeners.Remove(listener); });
    }

    /// <summary>Persists a changed settings object (e.g. overlay positions) and notifies listeners.</summary>
    public void Save(ScoutSettings settings)
    {
        lock (_gate) _current = settings;
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
        Notify(settings);
    }

    private ScoutSettings? Load()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var text = File.ReadAllText(Path);
                var parsed = JsonSerializer.Deserialize<ScoutSettings>(text, Json);
                LastError = null;
                return parsed;
            }
            catch (IOException) { Thread.Sleep(50); }  // the editor is still writing
            catch (JsonException ex) { LastError = $"settings.json: {ex.Message}"; return null; }
        }
        return null;
    }

    private void Reload()
    {
        var loaded = Load();
        if (loaded is null) return;
        loaded = ScoutSettings.Migrate(loaded);
        lock (_gate) _current = loaded;
        Notify(loaded);
    }

    private void Notify(ScoutSettings s)
    {
        List<Action<ScoutSettings, string?>> copy;
        lock (_gate) copy = _listeners.ToList();
        foreach (var l in copy) { try { l(s, null); } catch { } }
    }

    public void Dispose() => _watcher?.Dispose();

    private sealed class Unsubscribe(Action a) : IDisposable { public void Dispose() => a(); }
}
