using System.Collections.Concurrent;
using System.Windows.Media;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Cues;
using DeadlockScout.Core.Laning;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Objectives;
using DeadlockScout.Core.PostMatch;
using DeadlockScout.Core.Session;

namespace DeadlockScout.App.ViewModels;

/// <summary>
/// Everything the overlay shows, written by <see cref="MatchSession"/> from background threads and read by the 1 Hz
/// overlay timer on the dispatcher through <see cref="Refresh"/>. Collections are only mutated inside Refresh/Set calls
/// that the window invokes on its thread.
/// </summary>
public sealed class OverlayState
{
    public StatusViewModel Status { get; } = new();
    public PaceStripViewModel Pace { get; } = new();
    public ScoutListViewModel Scout { get; } = new();
    public EnemyPaceViewModel EnemyPace { get; } = new();
    public BuildHintViewModel Build { get; } = new();
    public ObjectivesViewModel Objectives { get; } = new();
    public TimersViewModel Timers { get; } = new();
    /// <summary>The live game clock for the timers; replaced on Reset.</summary>
    public LiveClock Clock { get; private set; } = new();
    /// <summary>The timers' schedule and lead, set by the session from settings.</summary>
    public TimerSettings TimerSettings = new();
    /// <summary>The broadcast delay assumed when only the broadcast gives the clock: 0 while spectating, where you watch the broadcast itself.</summary>
    public TimeSpan AssumedDelay = TimeSpan.FromSeconds(75);
    /// <summary>The audio cues' switches, read each second so a change applies at once (set by the composition root).</summary>
    public Func<AudioSettings> AudioSettings = () => new();
    /// <summary>Cues to play: raised on the UI thread during <see cref="Refresh"/>, only in a live match (never for the demo).</summary>
    public event Action<IReadOnlyList<Cue>>? CuesFired;
    private CueTracker _cues = new();
    private IReadOnlyList<TimeSpan> _pushNowSpawns = Array.Empty<TimeSpan>();
    private readonly List<string> _soonObjectives = new();
    private TimeSpan? _cueClock;

    /// <summary>The demo lobby's clocks: the timers' at the laning phase, the objectives' just before the mid boss returns.</summary>
    public TimeSpan? DemoClock, DemoObjectiveClock;
    /// <summary>The urn's schedule, set by the session from settings (defaults 10:00, then every 5:00, checked on a real match).</summary>
    public TimeSpan UrnFirstSpawn = TimeSpan.FromMinutes(10), UrnInterval = TimeSpan.FromMinutes(5);
    /// <summary>Kept across matches: Reset() does not clear it (it puts the real one back after the demo).</summary>
    public PostMatchViewModel LastMatch { get; } = new();
    /// <summary>Kept across matches; the overlay shows it only while no match is live (hideout and menus).</summary>
    public SessionViewModel Session { get; } = new();
    /// <summary>Set by the match session; the session panel hides while it is true.</summary>
    public volatile bool InMatch;
    /// <summary>Watching someone else's match: the pace board covers both teams and tags each row with its team.</summary>
    public volatile bool Spectating;

    public SoulRateMeter Meter { get; private set; } = new();
    /// <summary>Mid boss and urn state from the Direct feed; replaced on Reset with <see cref="MidBossRespawn"/>.</summary>
    public ObjectiveTracker ObjectiveTracker { get; private set; } = new();
    public TimeSpan MidBossRespawn { get; set; } = ObjectiveTracker.DefaultMidBossRespawn;
    public volatile PaceCurve? Curve;
    /// <summary>Your own recent games on the hero, for the second pace line; null when there are too few.</summary>
    public volatile PaceCurve? PersonalCurve;
    /// <summary>The rank goal's curve on your hero (the tier above yours by default) and that tier's name, for the gate line.</summary>
    public volatile PaceCurve? GoalCurve;
    public string GoalRank = "";
    /// <summary>The live phase chips; off with <c>goal.phases</c>.</summary>
    public bool ShowPhases = true;
    public static readonly TimeSpan MatchRetention = TimeSpan.FromHours(2);
    public readonly ConcurrentDictionary<uint, PaceCurve> EnemyCurves = new();
    public readonly ConcurrentDictionary<uint, NetWorthTick> EnemyTicks = new();
    // Each enemy's broadcast souls over the match, for their lines on the sparkline (locked: written by the session, read by Refresh).
    private readonly ConcurrentDictionary<uint, SoulRateMeter> _enemyMeters = new();
    /// <summary>Each enemy's power-spike item ids (see <see cref="ItemSpikes"/>), and the items they hold with when each showed up.</summary>
    public readonly ConcurrentDictionary<uint, IReadOnlySet<long>> EnemySpikes = new();
    public readonly ConcurrentDictionary<uint, ItemTimeline> EnemyItems = new();
    /// <summary>Your own items this match (Direct feed), so the next-item nudge skips what you already have.</summary>
    public ItemTimeline MyItems { get; private set; } = new();
    /// <summary>Your usual big items on the hero, the goal rank's buy times and its name, for the next-item nudge.</summary>
    public volatile IReadOnlyList<UsualItem>? UsualBuild;
    public volatile IReadOnlyDictionary<long, ItemTiming>? RankItemTimings;
    public string NextItemRank = "";
    public TimeSpan LatestGameTime;
    public DateTimeOffset ScoutVisibleUntil = DateTimeOffset.MinValue;

    private Roster? _roster;
    private IReadOnlyList<ScoutRow>? _rows;
    private uint _me;
    private AssetCatalog? _assets;
    private volatile bool _rosterDirty;
    private readonly ConcurrentDictionary<uint, HeroMatchup> _matchups = new(); // lane opponent → your hero's record against theirs
    private volatile IReadOnlyDictionary<uint, int> _parties = new Dictionary<uint, int>(); // account → likely party number
    private volatile BuildTables? _buildTables;
    private volatile AbilityPlan? _abilityPlan;
    // The demo lobby borrows the panels. The real session line and last-match report are kept here while it shows, and
    // put back when it ends; live updates that arrive meanwhile land here too.
    private readonly object _demoGate = new();
    private volatile bool _isDemo;
    private (SessionView View, AssetCatalog Assets)? _realSession;
    private (PostMatchReport Report, AssetCatalog Assets)? _realLastMatch;
    private string _heroBeforeDemo = "";

    /// <summary>True while the demo lobby fills the panels. A match start, <see cref="EndDemo"/> or any Reset ends it.</summary>
    public bool IsDemo => _isDemo;

    /// <summary>Clears the match data for a fresh fill; if the demo lobby was showing, it ends and the real session and last match come back.</summary>
    public void Reset()
    {
        lock (_demoGate) ResetCore();
    }

    /// <summary>
    /// Clears the panels for the demo lobby. During a match the caller pauses the match session first and resyncs it
    /// when the demo ends (see <c>MatchSession.Resync</c>).
    /// </summary>
    public void BeginDemo()
    {
        lock (_demoGate)
        {
            ResetCore();
            _heroBeforeDemo = Status.HeroName;
            _isDemo = true;
            Status.DemoButton = "Hide demo lobby";
        }
    }

    /// <summary>Takes the demo lobby down, if it is up, and restores what was there before. Never touches live match data.</summary>
    public void EndDemo()
    {
        lock (_demoGate)
            if (_isDemo) ResetCore();
    }

    private void ResetCore()
    {
        if (_isDemo)
        {
            _isDemo = false;
            Status.DemoButton = "Show demo lobby";
            Status.HeroName = _heroBeforeDemo;
            ScoutVisibleUntil = DateTimeOffset.MinValue;
            if (_realSession is { } s) SetSession(s.View, s.Assets); else Session.HasData = false;
            if (_realLastMatch is { } m) SetLastMatch(m.Report, m.Assets); else LastMatch.HasData = false;
        }
        // The whole match stays in the meter (a sample every few seconds is a few thousand at most), so the live phases
        // can read your souls at 10:00 and 20:00 long after.
        Meter = new SoulRateMeter(TimeSpan.FromSeconds(Math.Max(30, WindowSeconds)), MatchRetention);
        ObjectiveTracker = new ObjectiveTracker(MidBossRespawn);
        Objectives.HasData = false;
        Clock = new LiveClock();
        DemoClock = DemoObjectiveClock = null;
        _cues = new CueTracker();
        Timers.HasData = false;
        Curve = null;
        PersonalCurve = null;
        GoalCurve = null;
        _matchups.Clear();
        _parties = new Dictionary<uint, int>();
        _buildTables = null;
        _abilityPlan = null;
        Build.HasData = false;
        EnemyCurves.Clear();
        EnemyTicks.Clear();
        _enemyMeters.Clear();
        EnemySpikes.Clear();
        EnemyItems.Clear();
        MyItems = new ItemTimeline();
        UsualBuild = null;
        RankItemTimings = null;
        LatestGameTime = TimeSpan.Zero;
        _roster = null; _rows = null;
        _rosterDirty = true;
        Pace.HasData = false;
        Pace.Line = "";
        Pace.AsOf = "";
    }

    public int WindowSeconds { get; set; } = 120;
    /// <summary>Icons for the spike chips; null in tests, where every chip keeps its placeholder.</summary>
    public ItemIcons? Icons { get; init; }

    /// <summary>Every account id and name in the current lobby, for the diagnostics report to leave out.</summary>
    public IEnumerable<string> RosterSecrets()
    {
        if (_roster is not { } r) yield break;
        foreach (var p in r.Players)
        {
            yield return p.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            yield return p.Name;
        }
    }

    public void SetRoster(Roster roster, IReadOnlyList<ScoutRow> rows, uint me, AssetCatalog assets)
    {
        _roster = roster; _rows = rows; _me = me; _assets = assets;
        _rosterDirty = true;
    }

    /// <summary>Formats the session view. Safe from any thread (scalar bindings only). While the demo shows, a real update is held for after it.</summary>
    public void SetSession(SessionView v, AssetCatalog assets, bool demo = false)
    {
        if (!demo)
        {
            _realSession = (v, assets);
            if (_isDemo) return;
        }
        var t = v.Today;
        var parts = new List<string> { t.Games == 0 ? "No games yet today" : $"Today {t.Wins}–{t.Losses}" };
        if (Math.Abs(t.Streak) >= 2) parts.Add(t.Streak > 0 ? $"won {t.Streak} in a row" : $"lost {-t.Streak} in a row");
        if (t.BadgeNow is { } badge)
            parts.Add(Format.Rank(badge) + (t.BadgeChange is { } change && change != 0 ? $" ({(change > 0 ? "+" : "−")}{Math.Abs(change)} today)" : ""));
        Session.Today = string.Join(" · ", parts);
        Session.Nudge = v.Nudge ?? "";
        Session.HeroPool = v.HeroPool.Count == 0 ? "" : "Hero pool vs meta at your rank: " + string.Join(" · ", v.HeroPool.Select(h =>
            $"{assets.HeroName(h.HeroId)} {Format.Count(h.Matches)} {Format.Pct(h.WinRate)}" +
            (h.VersusMeta is { } d ? $" ({(d >= 0 ? "+" : "−")}{Math.Abs(d * 100):0} vs meta)" : "")));
        Session.HasData = true;
    }

    /// <summary>Formats a finished match's report for the main window. Safe from any thread (scalar bindings only). Held like <see cref="SetSession"/> while the demo shows.</summary>
    public void SetLastMatch(PostMatchReport r, AssetCatalog assets, bool demo = false)
    {
        if (!demo)
        {
            _realLastMatch = (r, assets);
            if (_isDemo) return;
        }
        var hero = assets.HeroName(r.HeroId);
        LastMatch.Title = $"{hero} · {(r.Won ? "Won" : "Lost")} · {Format.Clock(r.Duration)} · {r.Kills}/{r.Deaths}/{r.Assists} · " +
                          $"{r.NetWorth:N0} souls ({r.SoulsPerMinute:0}/min) · {Format.Ordinal(r.TeamNetWorthRank)} of {r.TeamSize} on team";
        LastMatch.Pace = r.Pace.Count == 0 ? "" : r.BenchmarkProvider is null
            ? "Pace: no benchmark for this hero yet"
            : "Pace at " + string.Join(" · ", r.Pace.Select(p =>
                $"{p.Minute}:00 {(p.Delta is { } d ? Format.Signed(d) : "–")}{(p.Percentile is { } pct ? $" ({Format.Ordinal((int)Math.Round(pct))} pct)" : "")}"));
        LastMatch.Lane = r.Lane is { } lane
            ? $"Lane at {lane.Minute}:00: {Format.SignedCount(lane.Difference)} souls against {string.Join(" & ", lane.OpponentHeroIds.Select(assets.HeroName))}"
            : "";
        LastMatch.Phases = PhaseLine(r);
        LastMatch.Footer = $"Match {r.MatchId} · {r.StartTime.ToLocalTime():ddd HH:mm}" +
                           (r.BenchmarkProvider is { } prov ? $" · benchmark: {prov} ({r.BenchmarkSampleSize} matches, badges {r.Bracket})" : "");
        LastMatch.Won = r.Won;
        LastMatch.HasData = true;
    }

    /// <summary>Fills the pace panel's phase slots from the meter and the goal curve; the current phase gets the catch-up hint.</summary>
    private void RefreshPhases()
    {
        var live = ShowPhases && GoalCurve is { } goal ? Phases.Live(Meter, goal) : Array.Empty<PhaseResult>();
        var now = Meter.Latest?.GameTime ?? TimeSpan.Zero;
        foreach (var (slot, i) in Pace.PhaseSlots.Select((s, i) => (s, i)))
        {
            var p = live.FirstOrDefault(r => (int)r.Phase == i);
            slot.Visible = p is not null;
            slot.Value = p?.Delta is { } d ? Format.Signed(d) : "–";
            slot.Delta = p?.Delta;
            slot.Current = p is not null && p.To == now && (Phases.End(p.Phase) is not { } end || now < end);
        }
        Pace.HasPhases = live.Count > 0;
        Pace.CatchUp = live.LastOrDefault() is { } cur && Pace.PhaseSlots[(int)cur.Phase].Current && GoalCurve is { } c
                       && Phases.CatchUpSpm(cur, c) is { } need && Phases.End(cur.Phase) is { } until
            ? $"need {need:0} spm to {Format.Clock(until)} to catch {GoalRank}"
            : "";
    }

    /// <summary>One game's phases against the rank goal, and the phase that cost the most souls; empty without the goal's curve.</summary>
    private static string PhaseLine(PostMatchReport r)
    {
        if (r.Phases is not { Count: > 0 } phases) return "";
        var line = $"Against {r.GoalRank}: " + string.Join(" · ", phases.Select(p =>
            $"{Phases.Name(p.Phase).ToLowerInvariant()} {(p.Delta is { } d ? Format.Signed(d) : "–")}"));
        if (phases.Where(p => p.Gained < p.Expected).OrderBy(p => p.Gained - p.Expected).FirstOrDefault() is { } worst)
            line += $" · most lost in {Phases.Name(worst.Phase).ToLowerInvariant()} ({Format.Count((int)Math.Round(worst.Expected - worst.Gained))} souls)";
        return line;
    }

    /// <summary>The hero's build tables; the ability plan is reduced once here, the item hint at every refresh.</summary>
    public void SetBuild(BuildTables tables)
    {
        _abilityPlan = BuildHints.Plan(tables.Abilities);
        _buildTables = tables;
    }

    public void SetMatchup(uint opponentAccountId, HeroMatchup matchup)
    {
        _matchups[opponentAccountId] = matchup;
        _rosterDirty = true;
    }

    /// <summary>Likely parties (account → group number), shown as "Team 1" chips next to the names.</summary>
    public void SetParties(IReadOnlyDictionary<uint, int> parties)
    {
        _parties = parties;
        _rosterDirty = true;
    }

    public void UpdateEnemy(NetWorthTick tick, RosterPlayer player, AssetCatalog assets)
    {
        _assets ??= assets;
        EnemyTicks[tick.AccountId] = tick;
        var meter = _enemyMeters.GetOrAdd(tick.AccountId, _ => new SoulRateMeter(Meter.Window, TimeSpan.FromMinutes(15)));
        lock (meter) meter.Add(new SoulSample(tick.GameTime, tick.NetWorth, SampleSource.LiveFeed));
        if (tick.Items is { } items)
            EnemyItems.GetOrAdd(tick.AccountId, _ => new ItemTimeline())
                .Observe(tick.UltimateTrained == true ? items.Append(ItemSpikes.UltimateId) : items, tick.GameTime);
    }

    /// <summary>Called on the UI thread once a second (and on a foreground change): recomputes derived values from samples and curves (never stored).</summary>
    public void Refresh()
    {
        RefreshPace();
        RefreshBuild();
        RefreshObjectives();
        RefreshTimers();
        if (_rosterDirty) { _rosterDirty = false; RebuildScout(); }
        RefreshEnemyPace();
        RefreshCues();
    }

    /// <summary>Hands this second's timers, objectives and pace to the cue tracker and raises what it decides to play.</summary>
    private void RefreshCues()
    {
        if (!InMatch || _isDemo || _cueClock is not { } now) return;
        var cues = _cues.Update(new CueInput(now, _pushNowSpawns, _soonObjectives.ToList(), Pace.GoalDelta), AudioSettings());
        if (cues.Count > 0) CuesFired?.Invoke(cues);
    }

    /// <summary>The timers' rows at the live clock: a countdown to each spawn, gold inside the lead, green once up.</summary>
    private void RefreshTimers()
    {
        var read = DemoClock is { } demo ? (demo, ClockSource.Screen) : Clock.Read(DateTimeOffset.UtcNow, AssumedDelay);
        var rows = read is { } r ? SpawnTimers.Rows(r.GameTime, TimerSettings) : Array.Empty<TimerRow>();
        _cueClock = read?.GameTime;
        _pushNowSpawns = rows.Where(x => x.State == TimerState.PushNow).Select(x => x.At).ToList();
        for (int i = 0; i < Timers.Rows.Length; i++)
        {
            var slot = Timers.Rows[i];
            if (i >= rows.Count) { slot.Visible = false; continue; }
            var row = rows[i];
            slot.Time = Format.Clock(row.At);
            slot.Names = row.Names;
            slot.Note = row.Note;
            slot.Push = row.State == TimerState.PushNow;
            slot.Up = row.State == TimerState.Up;
            slot.Countdown = row.State switch
            {
                TimerState.Up => "up",
                TimerState.PushNow => $"push now · {Format.Clock(row.In)}",
                _ => $"in {Format.Clock(row.In)}",
            };
            slot.Visible = true;
        }
        Timers.ClockLine = read is not { } c ? "" : DemoClock is not null ? "demo clock" : c.Source switch
        {
            ClockSource.Screen => "live clock: HUD (OCR)",
            ClockSource.GameLog => "live clock: game log",
            _ => $"clock: broadcast + {AssumedDelay.TotalSeconds:0} s (approximate; calibrate the OCR timer box for exact)",
        };
        Timers.HasData = rows.Count > 0;
    }

    private void RefreshObjectives()
    {
        var view = ObjectiveTracker.Snapshot();
        int myTeam = Spectating ? 0 : _roster?.Find(_me)?.Team ?? 0;
        var (boss, rejuvs, urn) = view is null ? ("", "", "") : ObjectiveFormat.Lines(view, myTeam);
        Objectives.MidBoss = boss; Objectives.Rejuvs = rejuvs; Objectives.Urn = urn;

        // Countdowns on the live clock; the urn's needs no broadcast, so it shows on the hosted feed too.
        var now = DemoObjectiveClock ?? Clock.Read(DateTimeOffset.UtcNow, AssumedDelay)?.GameTime;
        var lead = TimeSpan.FromSeconds(Math.Max(0, TimerSettings.LeadSeconds));
        var bossIn = now is { } n1 ? ObjectiveCountdowns.MidBoss(view, n1, lead) : null;
        var urnIn = now is { } n2 && (InMatch || DemoObjectiveClock is not null) ? ObjectiveCountdowns.NextUrn(view, n2, UrnFirstSpawn, UrnInterval, lead) : null;
        _soonObjectives.Clear();
        if (bossIn is { Soon: true }) _soonObjectives.Add($"boss@{bossIn.At}");
        if (urnIn is { Soon: true }) _soonObjectives.Add($"urn@{urnIn.At}");
        Objectives.MidBossCountdown = bossIn?.Text ?? "";
        Objectives.MidBossSoon = bossIn?.Soon ?? false;
        Objectives.UrnCountdown = urnIn?.Text ?? "";
        Objectives.UrnSoon = urnIn?.Soon ?? false;
        Objectives.HasData = boss.Length + rejuvs.Length + urn.Length + Objectives.MidBossCountdown.Length + Objectives.UrnCountdown.Length > 0;
    }

    private void RefreshPace()
    {
        var reading = Meter.Read();
        if (reading is null) { Pace.HasData = false; return; }
        var hero = Status.HeroName is { Length: > 0 } h ? h : "You";
        var window = Meter.Window;
        if (Curve is { } curve)
        {
            var e = PaceMath.Evaluate(curve, reading);
            Pace.Line = PaceMath.FormatLine(hero, e, window);
            Pace.Delta = e.Delta;
            Pace.DeltaText = e.Delta is { } d ? Format.Signed(d) : "–";
            Pace.Target = $"pace {e.ExpectedSpm:0}";
            Pace.Percentile = e.Percentile is { } pct ? $"{Format.Ordinal((int)Math.Round(pct))} pct" : "";
            Pace.Sparkline = SparklinePoints(PaceMath.Sparkline(Meter, curve, PaceStripViewModel.SparkSpan, PaceStripViewModel.SparkStep), reading.GameTime);
            RefreshRivals(curve, reading.GameTime);
        }
        else
        {
            Pace.Line = $"{hero} · {PaceMath.FormatClock(reading.GameTime)} · {reading.WindowSpm:0} spm ({window.TotalMinutes:0}m) · no benchmark yet";
            Pace.Delta = null;
            Pace.DeltaText = "–";
            Pace.Target = "no benchmark yet";
            Pace.Percentile = "";
            if (Pace.Sparkline.Count > 0) Pace.Sparkline = new PointCollection();
            foreach (var r in Pace.Rivals) { r.Points = new PointCollection(); r.Label = ""; }
        }
        if (PersonalCurve is { } personal && PaceMath.Evaluate(personal, reading).Delta is { } mine)
        {
            Pace.PersonalLine = $"vs your last {personal.SampleSize} on {hero}: {Format.Signed(mine)}";
            Pace.PersonalDelta = mine;
        }
        else
        {
            Pace.PersonalLine = "";
            Pace.PersonalDelta = null;
        }
        if (GoalCurve is { } goal && PaceMath.Evaluate(goal, reading) is { ExpectedSpm: > 0 } g)
        {
            Pace.GoalLine = $"{GoalRank} gate {g.ExpectedSpm:0} spm · you {(g.Delta is { } gd ? Format.Signed(gd) : "–")}";
            Pace.GoalDelta = g.Delta;
        }
        else
        {
            Pace.GoalLine = "";
            Pace.GoalDelta = null;
        }
        RefreshPhases();
        Pace.Hero = hero;
        Pace.Clock = PaceMath.FormatClock(reading.GameTime);
        Pace.Spm = $"{reading.WindowSpm:0} spm ({(window.TotalMinutes >= 1 ? $"{window.TotalMinutes:0}m" : $"{window.TotalSeconds:0}s")})";
        Pace.AsOf = $"as of {PaceMath.FormatClock(reading.GameTime)}";
        Pace.HasData = true;
    }

    /// <summary>
    /// Two enemies' pace on your sparkline, against the same curve as yours so the lines share a scale (a line above
    /// yours is farming faster), each labelled with their souls against yours at their latest broadcast time.
    /// </summary>
    private void RefreshRivals(PaceCurve curve, TimeSpan now)
    {
        var picked = _roster is { } roster
            ? Rivals.Pick(roster, _me, EnemyTicks.ToDictionary(kv => kv.Key, kv => kv.Value.NetWorth))
            : Array.Empty<uint>();
        for (int i = 0; i < Pace.Rivals.Length; i++)
        {
            var line = Pace.Rivals[i];
            if (i >= picked.Count || !_enemyMeters.TryGetValue(picked[i], out var meter))
            {
                if (line.Points.Count > 0) line.Points = new PointCollection();
                line.Label = "";
                continue;
            }
            IReadOnlyList<(TimeSpan, double)> points;
            SoulSample? latest;
            lock (meter)
            {
                points = PaceMath.Sparkline(meter, curve, PaceStripViewModel.SparkSpan, PaceStripViewModel.SparkStep);
                latest = meter.Latest;
            }
            line.Points = SparklinePoints(points, now);
            var hero = _roster!.Find(picked[i]) is { } p ? _assets?.HeroName(p.HeroId) ?? p.Name : "Enemy";
            line.Label = latest is { } l ? $"{hero} {Rivals.Gap(l.Souls, Meter.InterpolateAt(l.GameTime))}" : hero;
        }
    }

    /// <summary>Maps (game time, delta) to the sparkline box: now at the right edge, ten minutes ago at the left, 0% in the middle.</summary>
    private static PointCollection SparklinePoints(IReadOnlyList<(TimeSpan GameTime, double Delta)> points, TimeSpan now)
    {
        const double w = PaceStripViewModel.SparkWidth, h = PaceStripViewModel.SparkHeight, range = PaceStripViewModel.SparkRange;
        var span = PaceStripViewModel.SparkSpan.TotalSeconds;
        var pc = new PointCollection(points.Count);
        foreach (var (t, d) in points)
        {
            double x = w - (now - t).TotalSeconds / span * w;
            double y = h / 2 - Math.Clamp(d / range, -1, 1) * (h / 2 - 1);
            pc.Add(new System.Windows.Point(Math.Max(0, x), y));
        }
        pc.Freeze();
        return pc;
    }

    private void RefreshBuild()
    {
        var tables = _buildTables;
        var reading = Meter.Read();
        RefreshNextItem(reading);
        if (tables is null || reading is null) { Build.HasData = Build.NextItem.Length > 0; return; }
        var hero = Status.HeroName is { Length: > 0 } h ? h : "your hero";

        var buys = BuildHints.CommonBuys(tables.Items, reading.Souls);
        if (buys.Count > 0)
        {
            int bucket = buys[0].Bucket;
            Build.Title = $"Bought at {Format.Count(bucket)}–{Format.Count(bucket + BuildHints.BucketWidth)} souls on {hero}";
            Build.Items = string.Join(" · ", buys.Select(b => $"{ItemName(b.ItemId)} {Format.Pct(b.WinRate)}"));
        }
        else
        {
            Build.Title = $"Build on {hero}";
            Build.Items = "";
        }

        if (_abilityPlan is { } plan)
        {
            var parts = new List<string>();
            if (plan.NextAfter(reading.GameTime) is { } next)
                parts.Add($"Next: {ItemName(next.AbilityId)} {next.Rank} (~{Format.Clock(next.TypicalTime)})");
            if (plan.MaxFirst is { } max) parts.Add($"max {ItemName(max)} first");
            Build.Abilities = string.Join(" · ", parts);
        }
        else Build.Abilities = "";

        Build.HasData = Build.Items.Length > 0 || Build.Abilities.Length > 0 || Build.NextItem.Length > 0;
    }

    /// <summary>
    /// "Next: Headhunter · Oracle has it by 12:07 · in 1:40": your next usual big item against the goal rank's buy time, on
    /// the live clock; gold inside the timers' lead, red once the rank is past it.
    /// </summary>
    private void RefreshNextItem(MeterReading? reading)
    {
        var now = Clock.Read(DateTimeOffset.UtcNow, AssumedDelay)?.GameTime ?? reading?.GameTime;
        if (UsualBuild is not { } usual || now is not { } t) { Build.NextItem = ""; Build.NextItemState = null; return; }
        var mine = MyItems.Snapshot();
        var owned = mine.Count > 0 ? mine.Keys.ToHashSet() : null;
        var lead = TimeSpan.FromSeconds(Math.Max(0, TimerSettings.LeadSeconds));
        if (NextItem.Next(usual, owned, t, RankItemTimings, lead) is not { } n) { Build.NextItem = ""; Build.NextItemState = null; return; }
        var name = ItemName(n.ItemId);
        Build.NextItem = n.State switch
        {
            NudgeState.NoRank => $"Next: {name} · you usually {Format.Clock(n.Usual.MedianAt)}",
            NudgeState.Late => $"Next: {name} · {NextItemRank} had it by {Format.Clock(n.RankAt!.Value)} ({Format.Clock(-n.In!.Value)} ago)",
            _ => $"Next: {name} · {NextItemRank} has it by {Format.Clock(n.RankAt!.Value)} · in {Format.Clock(n.In!.Value)}",
        };
        Build.NextItemState = n.State;
    }

    private string ItemName(long id) => _assets?.ItemName(id) ?? $"Item {id}";

    private void RebuildScout()
    {
        Scout.Amber.Clear(); Scout.Sapphire.Clear(); Scout.LaneOpponents.Clear();
        if (_roster is null || _rows is null) return;
        var me = _roster.Find(_me);
        foreach (var r in _rows)
        {
            var vm = new ScoutRowViewModel
            {
                AccountId = r.Player.AccountId,
                Name = r.Player.Name,
                Hero = _assets?.HeroName(r.Player.HeroId) ?? r.Player.HeroId.ToString(),
                Team = r.Player.Team,
                IsMe = r.Player.AccountId == _me,
                GamesOnHero = r.Flags.HasFlag(ScoutFlags.Private) ? "private" : $"{r.GamesOnHero} on hero / {r.TotalGames}",
                WinRate = r.GamesOnHero > 0 ? Format.Pct(r.WinRateOnHero) : "–",
                Rank = Format.Rank(r.Rank),
                Met = r.MetRecord is { } m ? $"{m.Wins}–{m.Losses}" : "",
                Flag = r.Flag ?? "",
                Lane = r.Player.Lane is { } l ? $"L{l}" : "",
                Matchup = _matchups.TryGetValue(r.Player.AccountId, out var matchup) ? Format.Matchup(matchup) : "",
                Party = _parties.TryGetValue(r.Player.AccountId, out var party) ? party : 0,
            };
            (r.Player.Team == Teams.Sapphire ? Scout.Sapphire : Scout.Amber).Add(vm);
            if (me is not null && r.Player.Team != me.Team && r.Player.Lane is not null && r.Player.Lane == me.Lane) Scout.LaneOpponents.Add(vm);
        }
        Scout.LaneTitle = me?.Lane is { } lane ? $"Lane {lane} opponents" : "Lane opponents";
        var amber = _roster.Team(Teams.Amber).Select(p => p.Rank).Where(r => r is > 0).Select(r => r!.Value).ToList();
        var sapphire = _roster.Team(Teams.Sapphire).Select(p => p.Rank).Where(r => r is > 0).Select(r => r!.Value).ToList();
        Scout.RankSpread = $"Amber {Spread(amber)} · Sapphire {Spread(sapphire)}";
    }

    private static string Spread(List<int> badges) => badges.Count == 0 ? "–" : $"{Format.Rank(badges.Min())} – {Format.Rank(badges.Max())}";

    private void RefreshEnemyPace()
    {
        EnemyPace.Title = Spectating ? "Pace vs curve (spectating)" : "Enemy pace";
        if (_roster is null) return;
        foreach (var (accountId, tick) in EnemyTicks)
        {
            var row = EnemyPace.Rows.FirstOrDefault(r => r.AccountId == accountId);
            if (row is null)
            {
                var p = _roster.Find(accountId);
                if (p is null) continue;
                var hero = _assets?.HeroName(p.HeroId) ?? p.HeroId.ToString();
                if (Spectating) hero = $"{Teams.Name(p.Team)[0]} · {hero}"; // A or S: both teams share the board
                row = new EnemyPaceRowViewModel { AccountId = accountId, Name = p.Name, Hero = hero };
                EnemyPace.Rows.Add(row);
            }
            row.NetWorthText = tick.NetWorth.ToString("N0");
            if (EnemyCurves.TryGetValue(accountId, out var curve) && curve.Points.Count > 1)
            {
                double expected = PaceMath.NetWorthAt(curve, tick.GameTime);
                row.Lead = expected > 0 ? tick.NetWorth / expected - 1 : 0;
                row.Line = $"{row.Hero} · {tick.NetWorth:N0} · {Format.Signed(row.Lead)} vs curve";
                row.DeltaText = Format.Signed(row.Lead);
            }
            else
            {
                row.Lead = 0;
                row.Line = $"{row.Hero} · {tick.NetWorth:N0}";
                row.DeltaText = "";
            }
            var spike = EnemyItems.TryGetValue(accountId, out var timeline) && EnemySpikes.TryGetValue(accountId, out var spikes)
                ? ItemSpikes.Latest(timeline.Snapshot(), spikes, id => _assets?.ItemCost(id) ?? 0, LatestGameTime > tick.GameTime ? LatestGameTime : tick.GameTime)
                : null;
            string name = spike is null ? "" : spike.ItemId == ItemSpikes.UltimateId ? "Ultimate" : ItemName(spike.ItemId);
            row.SpikeFlash = spike?.Flash == true;
            row.SpikeIcon = spike is null || spike.ItemId == ItemSpikes.UltimateId ? null : Icons?.Get(spike.ItemId);
            row.SpikeInitial = name.Length > 0 ? name[..1].ToUpperInvariant() : "";
            string? slot = spike is null ? null : spike.ItemId == ItemSpikes.UltimateId ? "ultimate" : _assets?.Item(spike.ItemId)?.Slot;
            if (slot != row.SpikeSlot)
            {
                row.SpikeSlot = slot;
                row.SpikeColor = Converters.SpikeColor(slot);
                row.SpikeFill = Converters.SpikeFill(slot);
            }
            row.SpikeText = spike is null ? "" : row.SpikeFlash ? $"just got {name}" : name;
        }
        // sort by lead, descending
        var sorted = EnemyPace.Rows.OrderByDescending(r => r.Lead).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = EnemyPace.Rows.IndexOf(sorted[i]);
            if (current != i) EnemyPace.Rows.Move(current, i);
        }
    }
}
