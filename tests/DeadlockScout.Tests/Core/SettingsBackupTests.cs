using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class SettingsBackupTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

    private static ScoutSettings Mine() => ScoutSettings.Migrate(new ScoutSettings()) with
    {
        SteamPath = @"D:\Steam",
        Api = new ApiSettings { ApiKey = "secret-key", ApiKeyProtected = "dpapi1:AAAA" },
        Overlay = new OverlaySettings
        {
            PanelScale = 1.2,
            Positions = new() { ["1920x1080/pace"] = new PanelPosition(100, 40) },
            Presets = new() { ["laning"] = new LayoutPreset(new PanelSettings { Lane = false }, new(), 0.9) },
        },
        Timers = new TimerSettings { LeadSeconds = 45 },
    };

    [Fact]
    public void A_backup_holds_everything_but_the_api_key()
    {
        var text = SettingsBackup.Write(Mine(), "0.3.0", At);
        Assert.DoesNotContain("secret-key", text);
        Assert.DoesNotContain("dpapi1:", text);
        Assert.Contains(SettingsBackup.Kind, text);
        Assert.Contains("1920x1080/pace", text);
        Assert.Contains("laning", text);
    }

    [Fact]
    public void A_restore_brings_back_the_layout_and_keeps_this_pcs_key_and_folders()
    {
        var text = SettingsBackup.Write(Mine(), "0.3.0", At);
        var here = ScoutSettings.Migrate(new ScoutSettings()) with
        {
            SteamPath = @"C:\Program Files (x86)\Steam",
            Api = new ApiSettings { ApiKey = "this-pc-key", ApiKeyProtected = "dpapi1:BBBB" },
        };
        var result = SettingsBackup.Read(text, here);
        Assert.Null(result.Error);
        var s = result.Settings!;
        Assert.Equal(1.2, s.Overlay.PanelScale);
        Assert.Equal(new PanelPosition(100, 40), s.Overlay.Positions["1920x1080/pace"]);
        Assert.False(s.Overlay.Presets["laning"].Panels.Lane);
        Assert.Equal(45, s.Timers.LeadSeconds);
        Assert.Equal("this-pc-key", s.Api.ApiKey);
        Assert.Equal("dpapi1:BBBB", s.Api.ApiKeyProtected);
        Assert.Equal(@"C:\Program Files (x86)\Steam", s.SteamPath);
        Assert.Equal(("0.3.0", At), (result.AppVersion, result.SavedAt));
    }

    [Fact]
    public void Old_backups_are_brought_up_to_date_and_other_files_are_refused()
    {
        // A backup of a version-1 settings file: the scout list's old default is migrated like settings.json's.
        var old = SettingsBackup.Write(new ScoutSettings { Overlay = new OverlaySettings { Panels = new PanelSettings { Scout = ScoutPanelMode.Auto } } }, "0.1.0", At);
        var restored = SettingsBackup.Read(old, new ScoutSettings()).Settings!;
        Assert.Equal(ScoutPanelMode.Hotkey, restored.Overlay.Panels.Scout);
        Assert.Equal(ScoutSettings.CurrentVersion, restored.Version);

        Assert.Contains("not a DeadlockScout settings backup", SettingsBackup.Read("{ \"version\": 3 }", new ScoutSettings()).Error);
        Assert.Contains("not a settings backup", SettingsBackup.Read("{ nope", new ScoutSettings()).Error);
        var newer = SettingsBackup.Write(new ScoutSettings(), "9.0.0", At).Replace("\"format\": 1", "\"format\": 2");
        Assert.Contains("newer DeadlockScout (9.0.0)", SettingsBackup.Read(newer, new ScoutSettings()).Error);
        Assert.EndsWith(".dsbackup.json", SettingsBackup.FileName(At));
    }
}
