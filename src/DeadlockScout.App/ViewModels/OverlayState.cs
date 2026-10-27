using System.Collections.Concurrent;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.App.ViewModels;

/// <summary>
/// Everything the overlay shows, written by <see cref="MatchSession"/> from background threads and read by the 2 Hz
/// overlay timer on the dispatcher through <see cref="Refresh"/>. Collections are only mutated inside Refresh/Set calls
/// that the window invokes on its thread.
/// </summary>
public sealed class OverlayState
{
    public StatusViewModel Status { get; } = new();
    public PaceStripViewModel Pace { get; } = new();
    public ScoutListViewModel Scout { get; } = new();
    public EnemyPaceViewModel EnemyPace { get; } = new();

    public SoulRateMeter Meter { get; private set; } = new();
    public volatile PaceCurve? Curve;
    /// <summary>Your own recent games on the hero, for the second pace line; null when there are too few.</summary>
    public volatile PaceCurve? PersonalCurve;
    public readonly ConcurrentDictionary<uint, PaceCurve> EnemyCurves = new();
    public readonly ConcurrentDictionary<uint, NetWorthTick> EnemyTicks = new();
    public TimeSpan LatestGameTime;
    public DateTimeOffset ScoutVisibleUntil = DateTimeOffset.MinValue;

    private Roster? _roster;
    private IReadOnlyList<ScoutRow>? _rows;
    private uint _me;
    private AssetCatalog? _assets;
    private volatile bool _rosterDirty;
    private readonly ConcurrentDictionary<uint, HeroMatchup> _matchups = new(); // lane opponent → your hero's record against theirs

    public void Reset()
    {
        Meter = new SoulRateMeter(TimeSpan.FromSeconds(Math.Max(30, WindowSeconds)));
        Curve = null;
        PersonalCurve = null;
        _matchups.Clear();
        EnemyCurves.Clear();
        EnemyTicks.Clear();
        LatestGameTime = TimeSpan.Zero;
        _roster = null; _rows = null;
        _rosterDirty = true;
        Pace.HasData = false;
        Pace.Line = "";
        Pace.AsOf = "";
    }

    public int WindowSeconds { get; set; } = 120;

    public void SetRoster(Roster roster, IReadOnlyList<ScoutRow> rows, uint me, AssetCatalog assets)
    {
        _roster = roster; _rows = rows; _me = me; _assets = assets;
        _rosterDirty = true;
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
    }

    /// <summary>Called on the UI thread at 2 Hz: recomputes derived values from samples and curves (never stored).</summary>
    public void Refresh()
    {
        RefreshPace();
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
        }
        else
        {
            Pace.Line = $"{hero} · {PaceMath.FormatClock(reading.GameTime)} · {reading.WindowSpm:0} spm ({window.TotalMinutes:0}m) · no benchmark yet";
            Pace.Delta = null;
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
        Pace.AsOf = $"as of {PaceMath.FormatClock(reading.GameTime)}";
        Pace.HasData = true;
    }

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
        if (_roster is null) return;
        foreach (var (accountId, tick) in EnemyTicks)
        {
            var row = EnemyPace.Rows.FirstOrDefault(r => r.AccountId == accountId);
            if (row is null)
            {
                var p = _roster.Find(accountId);
                if (p is null) continue;
                row = new EnemyPaceRowViewModel { AccountId = accountId, Name = p.Name, Hero = _assets?.HeroName(p.HeroId) ?? p.HeroId.ToString() };
                EnemyPace.Rows.Add(row);
            }
            if (EnemyCurves.TryGetValue(accountId, out var curve) && curve.Points.Count > 1)
            {
                double expected = PaceMath.NetWorthAt(curve, tick.GameTime);
                row.Lead = expected > 0 ? tick.NetWorth / expected - 1 : 0;
                row.Line = $"{row.Hero} · {tick.NetWorth:N0} · {Format.Signed(row.Lead)} vs curve";
            }
            else
            {
                row.Lead = 0;
                row.Line = $"{row.Hero} · {tick.NetWorth:N0}";
            }
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
