using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadlockScout.Core.Settings;

/// <summary>What a restore found: the settings to apply, or why the file was refused.</summary>
public sealed record RestoreResult(ScoutSettings? Settings, string? Error, string? AppVersion = null, DateTimeOffset? SavedAt = null);

/// <summary>
/// Settings → Back up / Restore: every setting, the overlay layout and its presets included, in one file the player keeps.
/// The deadlock-api key is left out: on disk it is encrypted for this Windows account (<see cref="SettingsSecrets"/>)
/// and would not decrypt anywhere else, and a backup is a file that travels. A restore keeps the current key and this
/// PC's Steam and Deadlock folders (the backup may come from another PC). Pure, so tested.
/// </summary>
public static class SettingsBackup
{
    public const string Kind = "DeadlockScout settings backup";
    public const int Format = 1;
    public const string FileExtension = ".dsbackup.json";

    /// <summary>The settings file's own JSON shape (camelCase, enums as names, comments allowed), shared with settings.json.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record BackupFile
    {
        public string? Kind { get; init; }
        public int Format { get; init; }
        public string? AppVersion { get; init; }
        public DateTimeOffset? SavedAt { get; init; }
        public ScoutSettings? Settings { get; init; }
    }

    /// <summary>"DeadlockScout settings 2026-11-03.dsbackup.json".</summary>
    public static string FileName(DateTimeOffset at) => $"DeadlockScout settings {at.LocalDateTime:yyyy-MM-dd}{FileExtension}";

    /// <summary>The backup file's text: the settings without the API key, with the app version and when.</summary>
    public static string Write(ScoutSettings settings, string appVersion, DateTimeOffset at)
    {
        var file = new BackupFile
        {
            Kind = Kind,
            Format = Format,
            AppVersion = appVersion,
            SavedAt = at,
            Settings = settings with { Api = settings.Api with { ApiKey = null, ApiKeyProtected = null } },
        };
        return JsonSerializer.Serialize(file, Json);
    }

    /// <summary>
    /// Reads a backup: its settings brought up to this version (<see cref="ScoutSettings.Migrate"/>), with
    /// <paramref name="current"/>'s API key and folders kept. Refuses a file that is not a backup, or one from a newer
    /// format, with a line saying why.
    /// </summary>
    public static RestoreResult Read(string text, ScoutSettings current)
    {
        BackupFile? file;
        try { file = JsonSerializer.Deserialize<BackupFile>(text, Json); }
        catch (JsonException ex) { return new RestoreResult(null, $"That file is not a settings backup ({ex.Message})"); }
        if (file?.Kind != Kind || file.Settings is null)
            return new RestoreResult(null, "That file is not a DeadlockScout settings backup");
        if (file.Format > Format)
            return new RestoreResult(null, $"That backup comes from a newer DeadlockScout ({file.AppVersion ?? "unknown version"}); update this one first");
        var migrated = ScoutSettings.Migrate(file.Settings);
        var restored = migrated with
        {
            Api = migrated.Api with { ApiKey = current.Api.ApiKey, ApiKeyProtected = current.Api.ApiKeyProtected },
            SteamPath = current.SteamPath,
            DeadlockPath = current.DeadlockPath,
        };
        return new RestoreResult(restored, null, file.AppVersion, file.SavedAt);
    }
}
