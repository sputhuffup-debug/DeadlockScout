using System.Collections.Concurrent;
using System.Windows.Media;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
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
    /// <summary>Kept across matches: Reset() does not clear it.</summary>
    public PostMatchViewModel LastMatch { get; } = new();
    /// <summary>Kept across matches; the overlay shows it only while no match is live (hideout and menus).</summary>
    public SessionViewModel Session { get; } = new();
    /// <summary>Set by the match session; the session panel hides while it is true.</summary>
    public volatile bool InMatch;
    /// <summary>Watching someone else's match: the pace board covers both teams and tags each row with its team.</summary>
    public volatile bool Spectating;

    public SoulRateMeter Meter { get; private set; } = new();
    public volatile PaceCurve? Curve;
    /// <summary>Your own recent games on the hero, for the second pace line; null when there are too few.</summary>
    public volatile PaceCurve? PersonalCurve;
    public readonly ConcurrentDictionary<uint, PaceCurve> EnemyCurves = new();
    public readonly ConcurrentDictionary<uint, NetWorthTick> EnemyTicks = new();
    /// <summary>Each enemy's power-spike item ids (see <see cref="ItemSpikes"/>), and the items they hold with when each showed up.</summary>
    public readonly ConcurrentDictionary<uint, IReadOnlySet<long>> EnemySpikes = new();
    public readonly ConcurrentDictionary<uint, ItemTimeline> EnemyItems = new();
    public TimeSpan LatestGameTime;
    public DateTimeOffset ScoutVisibleUntil = DateTimeOffset.MinValue;

    private Roster? _roster;
    private IReadOnlyList<ScoutRow>? _rows;
    private uint _me;
    private AssetCatalog? _assets;
    private volatile bool _rosterDirty;
    private readonly ConcurrentDictionary<uint, HeroMatchup> _matchups = new(); // lane opponent → your hero's record against theirs
    private volatile BuildTables? _buildTables;
    private volatile AbilityPlan? _abilityPlan;

    public void Reset()
    {
        Meter = new SoulRateMeter(TimeSpan.FromSeconds(Math.Max(30, WindowSeconds)));
        Curve = null;
        PersonalCurve = null;
        _matchups.Clear();
        _buildTables = null;
        _abilityPlan = null;
        Build.HasData = false;
        EnemyCurves.Clear();
        EnemyTicks.Clear();
        EnemySpikes.Clear();
        EnemyItems.Clear();
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

    public void SetRoster(Roster roster, IReadOnlyList<ScoutRow> rows, uint me, AssetCatalog assets)
    {
        _roster = roster; _rows = rows; _me = me; _assets = assets;
        _rosterDirty = true;
    }

    /// <summary>Formats the session view. Safe from any thread (scalar bindings only).</summary>
    public void SetSession(SessionView v, AssetCatalog assets)
    {
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

    /// <summary>Formats a finished match's report for the main window. Safe from any thread (scalar bindings only).</summary>
    public void SetLastMatch(PostMatchReport r, AssetCatalog assets)
    {
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
        LastMatch.Footer = $"Match {r.MatchId} · {r.StartTime.ToLocalTime():ddd HH:mm}" +
                           (r.BenchmarkProvider is { } prov ? $" · benchmark: {prov} ({r.BenchmarkSampleSize} matches, badges {r.Bracket})" : "");
        LastMatch.Won = r.Won;
        LastMatch.HasData = true;
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

    public void UpdateEnemy(NetWorthTick tick, RosterPlayer player, AssetCatalog assets)
    {
        _assets ??= assets;
        EnemyTicks[tick.AccountId] = tick;
        if (tick.Items is { } items)
            EnemyItems.GetOrAdd(tick.AccountId, _ => new ItemTimeline())
                .Observe(tick.UltimateTrained == true ? items.Append(ItemSpikes.UltimateId) : items, tick.GameTime);
    }

    /// <summary>Called on the UI thread once a second (and on a foreground change): recomputes derived values from samples and curves (never stored).</summary>
    public void Refresh()
    {
        RefreshPace();
        RefreshBuild();
        if (_rosterDirty) { _rosterDirty = false; RebuildScout(); }
        RefreshEnemyPace();
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
        }
        else
        {
            Pace.Line = $"{hero} · {PaceMath.FormatClock(reading.GameTime)} · {reading.WindowSpm:0} spm ({window.TotalMinutes:0}m) · no benchmark yet";
            Pace.Delta = null;
            Pace.DeltaText = "–";
            Pace.Target = "no benchmark yet";
            Pace.Percentile = "";
            if (Pace.Sparkline.Count > 0) Pace.Sparkline = new PointCollection();
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
        Pace.Hero = hero;
        Pace.Clock = PaceMath.FormatClock(reading.GameTime);
        Pace.Spm = $"{reading.WindowSpm:0} spm ({(window.TotalMinutes >= 1 ? $"{window.TotalMinutes:0}m" : $"{window.TotalSeconds:0}s")})";
        Pace.AsOf = $"as of {PaceMath.FormatClock(reading.GameTime)}";
        Pace.HasData = true;
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
        if (tables is null || reading is null) { Build.HasData = false; return; }
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

        Build.HasData = Build.Items.Length > 0 || Build.Abilities.Length > 0;
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
