using System.Text.Json;
using System.Text.Json.Serialization;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class SettingsMigrationTests
{
    // The same shape as the app's settings.json (JsonSettingsMonitor.Json lives in the WPF project).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void The_scout_list_waits_for_the_hotkey_by_default()
        => Assert.Equal(ScoutPanelMode.Hotkey, new ScoutSettings().Overlay.Panels.Scout);

    [Fact]
    public void An_old_file_with_the_old_auto_default_moves_to_the_hotkey()
    {
        // What every settings.json written before versioning looks like: the old default spelled out, no version.
        var old = JsonSerializer.Deserialize<ScoutSettings>("""{ "overlay": { "panels": { "scout": "auto", "pace": true } } }""", Json)!;
        Assert.Equal(0, old.Version);
        var migrated = ScoutSettings.Migrate(old);
        Assert.Equal(ScoutPanelMode.Hotkey, migrated.Overlay.Panels.Scout);
        Assert.Equal(ScoutSettings.CurrentVersion, migrated.Version);
        Assert.True(migrated.Overlay.Panels.Pace);
    }

    [Fact]
    public void The_first_builds_one_live_url_an_hour_is_raised()
    {
        // Written by the first build: one lookup an hour, so a second spectated match waited up to an hour.
        var old = JsonSerializer.Deserialize<ScoutSettings>("""{ "version": 2, "api": { "limits": { "liveUrlPerHour": 1, "analyticsPerMin": 60 } } }""", Json)!;
        var migrated = ScoutSettings.Migrate(old);
        Assert.Equal(new RateLimitSettings().LiveUrlPerHour, migrated.Api.Limits.LiveUrlPerHour);
        Assert.Equal(60, migrated.Api.Limits.AnalyticsPerMin);
        Assert.Equal(ScoutSettings.CurrentVersion, migrated.Version);
        // A limit set after this version is the user's choice.
        var chosen = JsonSerializer.Deserialize<ScoutSettings>($$"""{ "version": {{ScoutSettings.CurrentVersion}}, "api": { "limits": { "liveUrlPerHour": 1 } } }""", Json)!;
        Assert.Equal(1, ScoutSettings.Migrate(chosen).Api.Limits.LiveUrlPerHour);
    }

    [Fact]
    public void A_deliberate_choice_is_kept()
    {
        var on = ScoutSettings.Migrate(JsonSerializer.Deserialize<ScoutSettings>("""{ "overlay": { "panels": { "scout": "on" } } }""", Json)!);
        Assert.Equal(ScoutPanelMode.On, on.Overlay.Panels.Scout);
        // Auto chosen after versioning (version 2 in the file) stays Auto.
        var auto = JsonSerializer.Deserialize<ScoutSettings>("""{ "version": 2, "overlay": { "panels": { "scout": "auto" } } }""", Json)!;
        Assert.Equal(ScoutPanelMode.Auto, ScoutSettings.Migrate(auto).Overlay.Panels.Scout);
        // A new file is written at the current version, with the mode spelled the way settings.json spells it.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ScoutSettings.Migrate(new ScoutSettings()), Json));
        Assert.Equal(ScoutSettings.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("hotkey", doc.RootElement.GetProperty("overlay").GetProperty("panels").GetProperty("scout").GetString());
    }
}
