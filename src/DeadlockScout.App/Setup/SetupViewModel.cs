using System.Collections.ObjectModel;
using System.Windows.Media;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Setup;
using DeadlockScout.Steam;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using CheckState = DeadlockScout.Core.Setup.CheckState;

namespace DeadlockScout.App.Setup;

/// <summary>A step in the wizard's list: its name and how it stands (a tick, a warning or a cross).</summary>
public sealed class SetupStepItem : ObservableObject
{
    private CheckState? _state;
    private bool _current;
    public SetupStepItem(string title) => Title = title;
    public string Title { get; }
    /// <summary>Null for steps with nothing to check (welcome, try it).</summary>
    public CheckState? State { get => _state; set { if (Set(ref _state, value)) { Raise(nameof(Mark)); Raise(nameof(MarkBrush)); } } }
    public bool Current { get => _current; set => Set(ref _current, value); }
    public string Mark => State switch { CheckState.Ok => "✓", CheckState.Warning => "!", CheckState.Missing => "✕", _ => "•" };
    public Brush MarkBrush => SetupViewModel.BrushFor(State);
}

/// <summary>
/// The first-run wizard over what the app finds on disk: Steam, Deadlock and your account, -condebug, Deadlock's display
/// mode, and OCR. It fixes what it may (typed paths, -condebug while Steam is closed, the OCR switch, all through
/// settings.json) and says what only the player can change (the game's display mode, which the app never touches).
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private static readonly Brush Ok = Frozen(Color.FromRgb(0x7F, 0xE0, 0x8A)), Warn = Frozen(Color.FromRgb(0xF2, 0xB1, 0x34)),
                                  Bad = Frozen(Color.FromRgb(0xF0, 0x7A, 0x6E)), Neutral = Frozen(Color.FromRgb(0x8C, 0x99, 0xA6));
    private readonly GamePaths _paths;
    private readonly JsonSettingsMonitor _settings;
    private SetupCheck _steam = null!, _condebug = null!, _display = null!, _ocr = null!;
    private string _steamInput = "", _deadlockInput = "", _accountInput = "", _condebugResult = "";

    public SetupViewModel(GamePaths paths, JsonSettingsMonitor settings)
    {
        _paths = paths;
        _settings = settings;
        var s = settings.CurrentValue;
        _steamInput = s.SteamPath ?? paths.SteamPath ?? "";
        _deadlockInput = s.DeadlockPath ?? paths.DeadlockPath ?? "";
        _accountInput = s.AccountId?.ToString() ?? (paths.AccountId is > 0 and var a ? a.ToString() : "");
        Recheck();
    }

    public ObservableCollection<SetupStepItem> Steps { get; } = new()
    {
        new("Welcome"), new("Steam and account"), new("Match detection"), new("Display mode"), new("OCR (optional)"), new("Try it"),
    };

    public SetupCheck SteamCheck { get => _steam; private set => Set(ref _steam, value); }
    public SetupCheck CondebugCheck { get => _condebug; private set => Set(ref _condebug, value); }
    public SetupCheck DisplayCheck { get => _display; private set => Set(ref _display, value); }
    public SetupCheck OcrCheck { get => _ocr; private set => Set(ref _ocr, value); }
    public Brush SteamBrush => BrushFor(SteamCheck.State);
    public Brush CondebugBrush => BrushFor(CondebugCheck.State);
    public Brush DisplayBrush => BrushFor(DisplayCheck.State);
    public Brush OcrBrush => BrushFor(OcrCheck.State);

    public string SteamInput { get => _steamInput; set => Set(ref _steamInput, value); }
    public string DeadlockInput { get => _deadlockInput; set => Set(ref _deadlockInput, value); }
    public string AccountInput { get => _accountInput; set => Set(ref _accountInput, value); }
    public string CondebugResult { get => _condebugResult; private set => Set(ref _condebugResult, value); }
    public string VideoConfigPath => _paths.DeadlockPath is { } d ? VideoConfig.PathFor(d) : "(Deadlock folder not found yet)";

    public bool OcrEnabled
    {
        get => _settings.CurrentValue.Ocr.Enabled;
        set
        {
            var s = _settings.CurrentValue;
            _settings.Save(s with { Ocr = s.Ocr with { Enabled = value } });
            Recheck();
        }
    }

    /// <summary>Looks at the disk again: after a typed path, -condebug, a change in the game's video settings or a calibration.</summary>
    public void Recheck()
    {
        var s = _settings.CurrentValue;
        SteamCheck = SetupChecks.Steam(_paths.SteamPath, _paths.DeadlockPath, _paths.AccountId);
        CondebugCheck = SetupChecks.Condebug(_paths.HasCondebug, _paths.SteamPath is not null && _paths.AccountId != 0);
        DisplayCheck = SetupChecks.Display(VideoConfig.ReadFile(_paths.DeadlockPath));
        OcrCheck = SetupChecks.Ocr(s.Ocr.Enabled, s.Ocr.Regions.Souls is not null && s.Ocr.Regions.Timer is not null);
        Steps[1].State = SteamCheck.State;
        Steps[2].State = CondebugCheck.State;
        Steps[3].State = DisplayCheck.State;
        Steps[4].State = OcrCheck.State;
        foreach (var name in new[] { nameof(SteamBrush), nameof(CondebugBrush), nameof(DisplayBrush), nameof(OcrBrush), nameof(VideoConfigPath), nameof(OcrEnabled) })
            Raise(name);
    }

    /// <summary>Writes the typed Steam folder, Deadlock folder and account id (blank ones are left to detection) and checks again.</summary>
    public void ApplyPaths()
    {
        var s = _settings.CurrentValue;
        uint? account = uint.TryParse(AccountInput.Trim(), out var a) && a > 0 ? a : null;
        _settings.Save(s with
        {
            SteamPath = Blank(SteamInput) ? null : SteamInput.Trim(),
            DeadlockPath = Blank(DeadlockInput) ? null : DeadlockInput.Trim(),
            AccountId = account,
        });
        Recheck();
    }

    public void AddCondebug()
    {
        var steam = _paths.SteamPath; var acc = _paths.AccountId;
        if (steam is null || acc == 0) { CondebugResult = "Find Steam and your account on the previous step first."; return; }
        CondebugResult = LaunchOptions.EnsureCondebug(steam, acc) switch
        {
            LaunchOptions.Result.AlreadySet => "-condebug is already set.",
            LaunchOptions.Result.Written => "-condebug added. Start Steam and Deadlock again.",
            LaunchOptions.Result.SteamRunning => "Steam is running: close it fully (tray icon → Exit) and press the button again, or add -condebug in Steam → Deadlock → Properties → Launch options.",
            _ => "Steam's localconfig.vdf was not found; add -condebug in Steam → Deadlock → Properties → Launch options.",
        };
        Recheck();
    }

    /// <summary>Marks the wizard done, so it no longer opens at start-up.</summary>
    public void Finish()
    {
        var s = _settings.CurrentValue;
        if (!s.Launch.SetupDone) _settings.Save(s with { Launch = s.Launch with { SetupDone = true } });
    }

    public static Brush BrushFor(CheckState? state) => state switch
    {
        CheckState.Ok => Ok, CheckState.Warning => Warn, CheckState.Missing => Bad, _ => Neutral,
    };

    private static bool Blank(string s) => string.IsNullOrWhiteSpace(s);
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
