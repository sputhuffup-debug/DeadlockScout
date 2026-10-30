using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using DeadlockScout.App.Interop;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.App.Settings;

/// <summary>A choice in one of the settings window's drop-downs: what it shows and the value it stands for.</summary>
public sealed record Choice(string Text, string Value);

/// <summary>
/// The settings window's fields over <see cref="JsonSettingsMonitor"/>. Every change is written to settings.json at once
/// and applies live through the monitor, the same way the overlay's layout editor and the old main-window switches did.
/// Text fields read like a player types them (see <see cref="SettingsFields"/>); a value that does not read, or is out
/// of range, is not saved and the field shows the stored value again. A hand edit of settings.json shows here too.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly JsonSettingsMonitor _monitor;
    private ScoutSettings _s;
    private bool _saving;
    private string _status = "";

    public SettingsViewModel(JsonSettingsMonitor monitor)
    {
        _monitor = monitor;
        _s = monitor.CurrentValue;
        BuildRankChoices();
        BuildSpawns();
        BuildPresetChoices();
        monitor.OnChange((s, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Reload(s)));
    }

    /// <summary>"Saved", or why the last change could not be written.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    private void Reload(ScoutSettings s)
    {
        if (_saving || ReferenceEquals(s, _s)) return;
        _s = s;
        BuildRankChoices();
        BuildSpawns();
        BuildPresetChoices();
        Raise(string.Empty); // every field shows the file again
    }

    /// <summary>Applies <paramref name="edit"/> and saves; the calling property is raised again so its field shows what was stored.</summary>
    private void Edit(Func<ScoutSettings, ScoutSettings> edit, [CallerMemberName] string? name = null)
    {
        var next = edit(_s);
        if (next != _s)
        {
            _saving = true;
            try { _monitor.Save(next); _s = next; Status = "Saved to settings.json"; }
            catch (Exception ex) { Status = $"Could not save settings.json: {ex.Message}"; }
            finally { _saving = false; }
        }
        Raise(name!);
    }

    /// <summary>For text fields: saves when <paramref name="value"/> reads, else shows the stored value again.</summary>
    private void EditIf<T>(T? value, Func<ScoutSettings, T, ScoutSettings> edit, [CallerMemberName] string? name = null) where T : struct
    {
        if (value is { } v) Edit(s => edit(s, v), name);
        else { Status = "That value was not saved: check its format"; Raise(name!); }
    }

    private ScoutSettings Panels(Func<PanelSettings, PanelSettings> f) => _s with { Overlay = _s.Overlay with { Panels = f(_s.Overlay.Panels) } };

    // ---- Overlay ----
    public bool OverlayEnabled { get => _s.Overlay.Enabled; set => Edit(s => s with { Overlay = s.Overlay with { Enabled = value } }); }
    public bool PanelPace { get => _s.Overlay.Panels.Pace; set => Edit(_ => Panels(p => p with { Pace = value })); }
    public bool PanelSparkline { get => _s.Overlay.Panels.Sparkline; set => Edit(_ => Panels(p => p with { Sparkline = value })); }
    public bool PanelPersonal { get => _s.Overlay.Panels.Personal; set => Edit(_ => Panels(p => p with { Personal = value })); }
    public bool PanelLane { get => _s.Overlay.Panels.Lane; set => Edit(_ => Panels(p => p with { Lane = value })); }
    public bool PanelLaneWithScout { get => _s.Overlay.Panels.LaneWithScout; set => Edit(_ => Panels(p => p with { LaneWithScout = value })); }
    public bool PanelLaneForm { get => _s.Overlay.Panels.LaneForm; set => Edit(_ => Panels(p => p with { LaneForm = value })); }
    public bool PanelCounterItems { get => _s.Overlay.Panels.CounterItems; set => Edit(_ => Panels(p => p with { CounterItems = value })); }
    public bool PanelSparklineEnemies { get => _s.Overlay.Panels.SparklineEnemies; set => Edit(_ => Panels(p => p with { SparklineEnemies = value })); }
    public bool PanelEnemyPace { get => _s.Overlay.Panels.EnemyPace; set => Edit(_ => Panels(p => p with { EnemyPace = value })); }
    public bool PanelSpikes { get => _s.Overlay.Panels.Spikes; set => Edit(_ => Panels(p => p with { Spikes = value })); }
    public bool PanelBuild { get => _s.Overlay.Panels.Build; set => Edit(_ => Panels(p => p with { Build = value })); }
    public bool PanelNextItem { get => _s.Overlay.Panels.NextItem; set => Edit(_ => Panels(p => p with { NextItem = value })); }
    public bool PanelObjectives { get => _s.Overlay.Panels.Objectives; set => Edit(_ => Panels(p => p with { Objectives = value })); }
    public bool PanelTimers { get => _s.Overlay.Panels.Timers; set => Edit(_ => Panels(p => p with { Timers = value })); }
    public bool PanelSession { get => _s.Overlay.Panels.Session; set => Edit(_ => Panels(p => p with { Session = value })); }

    public IReadOnlyList<Choice> ScoutModes { get; } = new[]
    {
        new Choice("Only while the hotkey has it on", nameof(ScoutPanelMode.Hotkey)),
        new Choice("For a while after the roster arrives", nameof(ScoutPanelMode.Auto)),
        new Choice("Always", nameof(ScoutPanelMode.On)),
        new Choice("Never", nameof(ScoutPanelMode.Off)),
    };
    public Choice? ScoutMode
    {
        get => ScoutModes.FirstOrDefault(c => c.Value == _s.Overlay.Panels.Scout.ToString());
        set { if (value is not null && Enum.TryParse<ScoutPanelMode>(value.Value, out var m)) Edit(_ => Panels(p => p with { Scout = m })); }
    }
    public string ScoutAutoSeconds
    {
        get => _s.Overlay.ScoutAutoSeconds.ToString();
        set => EditIf(SettingsFields.ReadInt(value, 10, 600), (s, v) => s with { Overlay = s.Overlay with { ScoutAutoSeconds = v } });
    }
    public string HotkeyToggle
    {
        get => _s.Overlay.HotkeyToggle;
        set => EditIf(Hotkey.TryParse(value ?? "", out _, out _) ? 1 : (int?)null, (s, _) => s with { Overlay = s.Overlay with { HotkeyToggle = value!.Trim() } });
    }
    public string HotkeyLayout
    {
        get => _s.Overlay.HotkeyLayout;
        set => EditIf(Hotkey.TryParse(value ?? "", out _, out _) ? 1 : (int?)null, (s, _) => s with { Overlay = s.Overlay with { HotkeyLayout = value!.Trim() } });
    }
    /// <summary>60–160 for the slider; stored as 0.6–1.6. Also on the overlay editor bar.</summary>
    public double PanelSize
    {
        get => Math.Round(OverlayPresets.ClampScale(_s.Overlay.PanelScale) * 100);
        set => Edit(s => s with { Overlay = s.Overlay with { PanelScale = OverlayPresets.ClampScale(Math.Round(value / 5) * 5 / 100) } });
    }

    // ---- Layout presets ----
    public ObservableCollection<Choice> PresetChoices { get; } = new();
    private Choice? _preset;
    private string _presetName = "";
    private void BuildPresetChoices()
    {
        var keep = _preset?.Value;
        PresetChoices.Clear();
        foreach (var b in OverlayPresets.BuiltIn) PresetChoices.Add(new Choice($"{b.Name}: {b.Description}", b.Name));
        foreach (var name in _s.Overlay.Presets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) PresetChoices.Add(new Choice($"{name} (saved)", name));
        _preset = PresetChoices.FirstOrDefault(c => c.Value == keep) ?? PresetChoices.FirstOrDefault();
        Raise(nameof(SelectedPreset));
        Raise(nameof(CanDeletePreset));
    }
    public Choice? SelectedPreset { get => _preset; set { _preset = value; Raise(nameof(SelectedPreset)); Raise(nameof(CanDeletePreset)); } }
    public bool CanDeletePreset => _preset is { } p && !OverlayPresets.IsBuiltIn(p.Value);
    public string PresetName { get => _presetName; set => Set(ref _presetName, value); }

    /// <summary>Replaces the panel switches, moved panels and panel size with the chosen preset's.</summary>
    public void ApplyPreset()
    {
        if (_preset is null || OverlayPresets.Find(_s.Overlay, _preset.Value) is not { } preset) return;
        Edit(s => s with { Overlay = s.Overlay.Apply(preset) }, nameof(SelectedPreset));
        Raise(string.Empty);
        Status = $"Layout \"{_preset.Value}\" applied";
    }

    /// <summary>Saves the current panels, positions and size under <see cref="PresetName"/>.</summary>
    public void SavePreset()
    {
        var name = PresetName.Trim();
        if (!_s.Overlay.TrySave(name, out var saved))
        {
            Status = name.Length == 0 ? "Type a name for the layout first" : $"\"{name}\" is a built-in layout; pick another name";
            return;
        }
        Edit(s => s with { Overlay = saved }, nameof(PresetChoices));
        _preset = new Choice($"{name} (saved)", name);
        BuildPresetChoices();
        PresetName = "";
        Status = $"Layout \"{name}\" saved";
    }

    public void DeletePreset()
    {
        if (!CanDeletePreset) return;
        var name = _preset!.Value;
        Edit(s => s with { Overlay = s.Overlay.WithoutPreset(name) }, nameof(PresetChoices));
        _preset = null;
        BuildPresetChoices();
        Status = $"Layout \"{name}\" deleted";
    }

    public bool ExcludeFromCapture { get => _s.Overlay.ExcludeFromCapture; set => Edit(s => s with { Overlay = s.Overlay with { ExcludeFromCapture = value } }); }
    public bool PartyTags { get => _s.Flags.PartyTags; set => Edit(s => s with { Flags = s.Flags with { PartyTags = value } }); }

    // ---- Rank gate ----
    public ObservableCollection<Choice> RankChoices { get; } = new();
    private void BuildRankChoices()
    {
        RankChoices.Clear();
        RankChoices.Add(new Choice("The rank above mine", SettingsFields.NextRank));
        foreach (var t in RankGoal.Choices(_s.Goal.IncludeLowRanks)) RankChoices.Add(new Choice(RankGoal.Name(t), RankGoal.Name(t)));
    }
    public Choice? GoalRank
    {
        get => RankChoices.FirstOrDefault(c => c.Value.Equals(_s.Goal.Rank, StringComparison.OrdinalIgnoreCase)) ?? RankChoices.FirstOrDefault();
        set { if (value is not null) Edit(s => s with { Goal = s.Goal with { Rank = value.Value } }); }
    }
    public bool IncludeLowRanks
    {
        get => _s.Goal.IncludeLowRanks;
        set
        {
            Edit(s => s with { Goal = s.Goal with { IncludeLowRanks = value } });
            BuildRankChoices();
            Raise(nameof(GoalRank));
        }
    }
    public bool GoalOverlay { get => _s.Goal.Overlay; set => Edit(s => s with { Goal = s.Goal with { Overlay = value } }); }
    public bool GoalPhases { get => _s.Goal.Phases; set => Edit(s => s with { Goal = s.Goal with { Phases = value } }); }

    // ---- Laning timers ----
    public string TimerLead { get => _s.Timers.LeadSeconds.ToString(); set => EditIf(SettingsFields.ReadInt(value, 0, 120), (s, v) => s with { Timers = s.Timers with { LeadSeconds = v } }); }
    public string TimerShowUp { get => _s.Timers.ShowUpSeconds.ToString(); set => EditIf(SettingsFields.ReadInt(value, 0, 120), (s, v) => s with { Timers = s.Timers with { ShowUpSeconds = v } }); }
    public string TimerRows { get => _s.Timers.MaxRows.ToString(); set => EditIf(SettingsFields.ReadInt(value, 1, 3), (s, v) => s with { Timers = s.Timers with { MaxRows = v } }); }
    public string BroadcastDelay { get => _s.Timers.BroadcastDelaySeconds.ToString(); set => EditIf(SettingsFields.ReadInt(value, 0, 300), (s, v) => s with { Timers = s.Timers with { BroadcastDelaySeconds = v } }); }
    public ObservableCollection<SpawnRowViewModel> Spawns { get; } = new();
    private void BuildSpawns()
    {
        Spawns.Clear();
        for (int i = 0; i < _s.Timers.Spawns.Count; i++) Spawns.Add(new SpawnRowViewModel(this, i, _s.Timers.Spawns[i]));
    }
    /// <summary>Called by a spawn row: replaces rule <paramref name="index"/>; false when the time does not read.</summary>
    internal bool EditSpawn(int index, SpawnRule rule)
    {
        if (index < 0 || index >= _s.Timers.Spawns.Count) return false;
        var list = _s.Timers.Spawns.ToList();
        list[index] = rule;
        Edit(s => s with { Timers = s.Timers with { Spawns = list } }, nameof(Spawns));
        return true;
    }
    public void RestoreSpawns()
    {
        Edit(s => s with { Timers = s.Timers with { Spawns = new TimerSettings().Spawns } }, nameof(Spawns));
        BuildSpawns();
    }

    // ---- Objectives ----
    public string MidBossRespawn { get => SettingsFields.Clock(_s.Objectives.MidBossRespawnSeconds); set => EditIf(SettingsFields.ReadClock(value, 30 * 60), (s, v) => s with { Objectives = s.Objectives with { MidBossRespawnSeconds = Math.Max(60, v) } }); }
    public string UrnFirst { get => SettingsFields.Clock(_s.Objectives.UrnFirstSpawnSeconds); set => EditIf(SettingsFields.ReadClock(value, 60 * 60), (s, v) => s with { Objectives = s.Objectives with { UrnFirstSpawnSeconds = v } }); }
    public string UrnInterval { get => SettingsFields.Clock(_s.Objectives.UrnIntervalSeconds); set => EditIf(SettingsFields.ReadClock(value, 30 * 60), (s, v) => s with { Objectives = s.Objectives with { UrnIntervalSeconds = v } }); }

    // ---- Audio ----
    public bool AudioOn { get => _s.Audio.Enabled; set => Edit(s => s with { Audio = s.Audio with { Enabled = value } }); }
    public bool AudioPush { get => _s.Audio.PushNow; set => Edit(s => s with { Audio = s.Audio with { PushNow = value } }); }
    public bool AudioObjectives { get => _s.Audio.Objectives; set => Edit(s => s with { Audio = s.Audio with { Objectives = value } }); }
    public bool AudioPace { get => _s.Audio.PaceBehind; set => Edit(s => s with { Audio = s.Audio with { PaceBehind = value } }); }
    /// <summary>0–100 for the slider; stored as 0–1.</summary>
    public double Volume { get => Math.Round(_s.Audio.Volume * 100); set => Edit(s => s with { Audio = s.Audio with { Volume = Math.Round(Math.Clamp(value, 0, 100)) / 100 } }); }
    public string PaceBehindPercent { get => _s.Audio.PaceBehindPercent.ToString(); set => EditIf(SettingsFields.ReadInt(value, 1, 50), (s, v) => s with { Audio = s.Audio with { PaceBehindPercent = v } }); }
    public string PaceBehindSeconds { get => _s.Audio.PaceBehindSeconds.ToString(); set => EditIf(SettingsFields.ReadInt(value, 10, 600), (s, v) => s with { Audio = s.Audio with { PaceBehindSeconds = v } }); }
    public string PaceCooldown { get => _s.Audio.PaceCooldownSeconds.ToString(); set => EditIf(SettingsFields.ReadInt(value, 30, 1800), (s, v) => s with { Audio = s.Audio with { PaceCooldownSeconds = v } }); }

    // ---- Live feed and API ----
    public IReadOnlyList<Choice> LiveSources { get; } = new[]
    {
        new Choice("Direct: the app reads Valve's broadcast", nameof(LiveSource.Direct)),
        new Choice("Hosted: deadlock-api.com's live query", nameof(LiveSource.Hosted)),
        new Choice("Self-hosted: your live-events container", nameof(LiveSource.SelfHosted)),
    };
    public Choice? LiveSourceChoice
    {
        get => LiveSources.FirstOrDefault(c => c.Value == _s.Api.LiveSource.ToString());
        set
        {
            if (value is null || !Enum.TryParse<LiveSource>(value.Value, out var src)) return;
            Edit(s => s.WithLiveSource(src));
            Raise(nameof(IsSelfHosted));
        }
    }
    public bool IsSelfHosted => _s.Api.LiveSource == LiveSource.SelfHosted;
    public string LiveUrl
    {
        get => _s.Api.LiveUrl;
        set => EditIf(Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? 1 : (int?)null,
            (s, _) => s with { Api = s.Api with { LiveUrl = value!.Trim() } });
    }
    public bool FallbackToHosted { get => _s.Api.DirectFallbackToHosted; set => Edit(s => s with { Api = s.Api with { DirectFallbackToHosted = value } }); }
    public string ApiKey { get => _s.Api.ApiKey ?? ""; set => Edit(s => s with { Api = s.Api with { ApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim() } }); }

    // ---- Benchmarks ----
    public string WindowSeconds { get => _s.Meter.WindowSeconds.ToString(); set => EditIf(SettingsFields.ReadInt(value, 30, 600), (s, v) => s with { Meter = s.Meter with { WindowSeconds = v } }); }
    public string BenchmarkBracket
    {
        get => _s.Meter.BenchmarkBracket;
        set => EditIf(ValidBracket(value) ? 1 : (int?)null, (s, _) => s with { Meter = s.Meter with { BenchmarkBracket = value!.Trim().ToLowerInvariant() } });
    }
    public string BracketWidth { get => _s.Meter.BracketWidth.ToString(); set => EditIf(SettingsFields.ReadInt(value, 0, 60), (s, v) => s with { Meter = s.Meter with { BracketWidth = v } }); }
    public string PrefetchHeroes { get => _s.Meter.PrefetchHeroes.ToString(); set => EditIf(SettingsFields.ReadInt(value, 0, 10), (s, v) => s with { Meter = s.Meter with { PrefetchHeroes = v } }); }

    /// <summary>"auto", "all", or "min-max" badge numbers with min ≤ max.</summary>
    public static bool ValidBracket(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        if (v is "auto" or "all") return true;
        var parts = v?.Split('-');
        return parts is { Length: 2 } && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi) && lo >= 0 && lo <= hi && hi <= 116;
    }

    // ---- OCR ----
    public bool OcrEnabled { get => _s.Ocr.Enabled; set => Edit(s => s with { Ocr = s.Ocr with { Enabled = value } }); }
    public string OcrRegions => (_s.Ocr.Regions.Souls is null ? "souls not set" : "souls set") + " · " +
                                (_s.Ocr.Regions.Timer is null ? "clock not set" : "clock set") + " · " +
                                $"{_s.Ocr.Regions.EnemySouls.Count(r => r is not null)} enemy rows";

    // ---- Session and launch ----
    public string DayStartsAt { get => _s.Session.DayStartsAtHour.ToString(); set => EditIf(SettingsFields.ReadInt(value, 0, 23), (s, v) => s with { Session = s.Session with { DayStartsAtHour = v } }); }
    public string LossStreakNudge { get => _s.Session.LossStreakNudge.ToString(); set => EditIf(SettingsFields.ReadInt(value, 0, 10), (s, v) => s with { Session = s.Session with { LossStreakNudge = v } }); }
    public string HeroPoolSize { get => _s.Session.HeroPoolSize.ToString(); set => EditIf(SettingsFields.ReadInt(value, 1, 10), (s, v) => s with { Session = s.Session with { HeroPoolSize = v } }); }
    public bool AutoSubmitSalts { get => _s.Ingest.AutoSubmitSalts; set => Edit(s => s with { Ingest = s.Ingest with { AutoSubmitSalts = value } }); }
    public bool EnsureCondebug { get => _s.Launch.EnsureCondebug; set => Edit(s => s with { Launch = s.Launch with { EnsureCondebug = value } }); }

    public string SettingsPath => _monitor.Path;
}

/// <summary>One laning-timer spawn in the settings window: name and first spawn time, written back as one rule.</summary>
public sealed class SpawnRowViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private readonly int _index;
    private SpawnRule _rule;

    public SpawnRowViewModel(SettingsViewModel owner, int index, SpawnRule rule) { _owner = owner; _index = index; _rule = rule; }

    public string Name
    {
        get => _rule.Name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && _owner.EditSpawn(_index, _rule with { Name = value.Trim() })) _rule = _rule with { Name = value.Trim() };
            Raise(nameof(Name));
        }
    }

    public string Time
    {
        get => SettingsFields.Clock(_rule.FirstSpawnSeconds);
        set
        {
            if (SettingsFields.ReadClock(value, 60 * 60) is { } t && _owner.EditSpawn(_index, _rule with { FirstSpawnSeconds = t })) _rule = _rule with { FirstSpawnSeconds = t };
            Raise(nameof(Time));
        }
    }

    public string Note => _rule.Note;
}
