using System.Diagnostics;
using System.IO;
using System.Windows;
using DeadlockScout.Api.Benchmarks;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.App.History;
using DeadlockScout.Core.History;
using DeadlockScout.App.Calibration;
using DeadlockScout.App.Interop;
using DeadlockScout.App.Overlay;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Diagnostics;
using DeadlockScout.App.Diagnostics;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Cues;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Laning;
using DeadlockScout.Core.Objectives;
using DeadlockScout.Core.PostMatch;
using DeadlockScout.Core.Session;
using DeadlockScout.Core.Settings;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;
using DeadlockScout.Steam;

namespace DeadlockScout.App.Services;

/// <summary>
/// The demo lobby: original content that fills every panel so the layout can be checked without a match, for the
/// main window, the overlay preview and the screenshots. Player names are made up; heroes are real ids so names resolve
/// from the assets endpoint ("You" plays Haze).
/// </summary>
public static class DemoLobby
{
    /// <summary>Fills <paramref name="state"/>, which must already be in demo mode (<see cref="OverlayState.BeginDemo"/>).</summary>
    public static void Fill(OverlayState state, AssetCatalog assets)
    {
        state.Status.HeroName = "Haze";
        var players = new List<RosterPlayer>();
        string[] names = { "You", "Vex", "Moth", "Quill", "Rook", "Sable", "Ash", "Brim", "Cinder", "Drift", "Ember", "Flint" };
        int[] heroes = { 13, 1, 6, 7, 11, 19, 2, 3, 4, 10, 12, 14 };
        for (int i = 0; i < 12; i++)
            players.Add(new RosterPlayer((uint)(1000 + i), names[i], i < 6 ? Teams.Amber : Teams.Sapphire, heroes[i], 60 + (i * 3) % 20, i % 4 + 1));
        var roster = new Roster(1, players, TimeSpan.FromSeconds(90));
        var rows = players.Select((p, i) => new ScoutRow(p, 5 + i * 7, 0.45 + i * 0.02, 120 + i * 30, p.Rank, i == 7 ? 2 : null, i == 9 ? "Smurf?" : i == 2 ? "new on hero" : null)
        {
            Flags = i == 9 ? ScoutFlags.SuspectedSmurf : i == 2 ? ScoutFlags.NewOnHero : ScoutFlags.None,
            MetRecord = i == 7 ? (1, 1) : null,
            CareerSpmOnHero = 700 + i * 15,
        }).ToList();
        state.SetRoster(roster, rows, 1000, assets);
        // The team lines' last 30 days, made up: Sapphire has played more lately and farms a little above its usual.
        state.SetTeamForm(players.Select((p, i) =>
        {
            int games = i < 6 ? 4 + i % 3 * 2 : 7 + i % 4 * 2;
            int wins = i < 6 ? games / 2 : games / 2 + 1;
            return new PlayerHeroStats(p.AccountId, p.HeroId, games, wins, 0, 0, 0) { NetWorthPerMin = (700 + i * 15) * (i < 6 ? 0.98 : 1.06) };
        }).ToList());
        // Likely parties from made-up party histories: you and Quill; Ash, Brim and Cinder; Ember and Flint.
        SharedHistory Mate(uint id, int games) => new(id, games, games / 2, games - games / 2, false);
        state.SetParties(PartyGroups.Detect(roster, new Dictionary<uint, IReadOnlyList<SharedHistory>>
        {
            [1000] = new[] { Mate(1003, 14) },
            [1006] = new[] { Mate(1007, 9), Mate(1008, 3) },
            [1010] = new[] { Mate(1011, 22) },
            [1004] = new[] { Mate(1009, 5) }, // partied before, but on the other team now: no tag
        }));
        state.ScoutVisibleUntil = DateTimeOffset.UtcNow.AddSeconds(90);
        var curve = new PaceCurve(1, RankBracket.All, "demo",
            Enumerable.Range(0, 9).Select(m => new PacePoint(m * 5, m * 5 * 540, m * 5 * 580, m * 5 * 620, m * 5 * 580)).ToList(), 40, "demo");
        state.Curve = curve;
        // Your own last 12 games on the hero, a little behind the population at this rank.
        state.PersonalCurve = curve with
        {
            Provider = "personal", SampleSize = 12,
            Points = curve.Points.Select(p => p with { Mean = (int)(p.Mean * 0.97) }).ToList(),
        };
        // The timers at a laning-phase moment: medium camps and Midtown crates 25 s away, so their row asks you to push.
        state.DemoClock = new TimeSpan(0, 4, 35);
        // A small camp next to the demo player, cleared at 3:20: back at 4:45, so its respawn row joins the panel.
        var demoCamps = new CampTracker();
        demoCamps.Observe(new TimeSpan(0, 2, 0), new[] { new NeutralSighting(1, 2000, 3000), new NeutralSighting(2, 2150, 3100), new NeutralSighting(3, 2050, 2900),
                                                         new NeutralSighting(4, -6000, 3000), new NeutralSighting(5, -6100, 3150) });
        demoCamps.Observe(new TimeSpan(0, 3, 20), new[] { new NeutralSighting(4, -6000, 3000), new NeutralSighting(5, -6100, 3150) });
        state.CampTracker = demoCamps;
        state.MyPosition = (1300f, 3400f);
        // The next-item nudge: Headhunter already bought, Fortitude next, which Oracle players have 24 s from now.
        const long hh = 4053935515, fort = 3585132399, lch = 865846625; // Headhunter, Fortitude, Leech
        state.UsualBuild = new[]
        {
            new UsualItem(hh, 9, new TimeSpan(0, 13, 10)), new UsualItem(fort, 7, new TimeSpan(0, 16, 30)),
            new UsualItem(lch, 6, new TimeSpan(0, 23, 0)),
        };
        state.RankItemTimings = new Dictionary<long, ItemTiming>
        {
            [hh] = new(hh, 900, 727), [fort] = new(fort, 700, 864), [lch] = new(lch, 500, 1274),
        };
        state.NextItemRank = "Oracle";
        state.MyItems.Observe(new[] { hh }, new TimeSpan(0, 12, 50));
        // ...and the objectives just before the demo mid boss's estimated return (18:20), so its countdown is gold.
        state.DemoObjectiveClock = new TimeSpan(0, 17, 55);
        // The rank above the demo player's: a slower lane than the demo player's, a faster mid game, so the phase chips show
        // a good lane, a mid game behind and the catch-up hint.
        state.GoalRank = "Oracle";
        // Goals from the match history: past laning, so the line speaks of the mid game, the phase chosen to fix.
        state.Targets = new TargetSettings { LaningSpm = 420, MaxLaningDeaths = 1, FixPhase = GamePhase.Mid };
        state.UnspentThreshold = UnspentSouls.DefaultThreshold;
        state.HauntedThreshold = UnspentSouls.DefaultHauntedThreshold;
        state.LatestSample = new SoulSample(TimeSpan.FromSeconds(840), 8_570, SampleSource.Ocr, 3_420, Haunted: 1_100);
        state.MyDeaths = 1;
        state.GoalCurve = curve with
        {
            Provider = "analytics", SampleSize = 900,
            Points = curve.Points.Select(p => new PacePoint(p.Minute, null, null, null,
                (int)(p.Minute <= 10 ? 568 * p.Minute : 5680 + 690 * (p.Minute - 10)))).ToList(),
        };
        // Haze's build tables: real item and ability ids so names resolve from the assets; the counts are made up.
        const long fixation = 1080948381, sleepDagger = 2948410412, smokeBomb = 2414191464, bulletDance = 731943444;
        long[] order = { fixation, fixation, sleepDagger, smokeBomb, fixation, sleepDagger, bulletDance, sleepDagger, sleepDagger, fixation, bulletDance };
        double[] times = { 13, 60, 105, 199, 254, 315, 365, 478, 790, 905, 1130 };
        state.SetBuild(new BuildTables(
            new[]
            {
                new ItemBucketStat(2739107182, 8000, 10843, 5800, 1054), new ItemBucketStat(3585132399, 8000, 7605, 3950, 1003),
                new ItemBucketStat(1292979587, 8000, 7446, 3700, 1006), new ItemBucketStat(2480592370, 8000, 5293, 2600, 1037),
            },
            new[] { new AbilityOrderStat(order, times, 5501, 3110) }));
        // Today so far, after a couple of losses: the nudge shows from three in a row by default.
        state.SetSession(new SessionView(new SessionSummary(4, 3, -3, 73, 72, DateTimeOffset.Now.Date), SessionMath.Nudge(new SessionSummary(4, 3, -3, 73, 72, DateTimeOffset.Now.Date), 3),
            new[]
            {
                // Haze ahead of the gate and rising; Abrams wins about as often as the meta but farms well under the gate.
                new HeroPoolRow(13, 214, 118, 0.512, new HeroPace(10, 0.06, 0.05, "Oracle")),
                new HeroPoolRow(1, 96, 45, 0.497, new HeroPace(10, -0.11, -0.04, "Oracle")),
                new HeroPoolRow(7, 51, 27, 0.505, new HeroPace(3, 0.01, null, "Oracle")),
            }, new RankBracket(63, 83),
            // Two made-up friends: one you farm better with, one you farm worse with.
            new[] { new TeammateRow(1, "Ava", 23, 14, 612, 0.04), new TeammateRow(2, "Ben", 12, 5, 540, -0.08) },
            // Climbing: Archon 3 now, Archon 4 about nine games away at the last twenty ranked games' rate.
            new RankForecast(73, RankDirection.Rising, 74, 9, 20)), assets, demo: true);
        // Last week, made up: a winning week with a rank up, laning and Headhunter better than the week before, and
        // mid game the thing to work on.
        state.SetWeekly(new WeeklyReport(DateOnly.FromDateTime(WeeklySummary.LastWeekStart(DateTimeOffset.Now, TimeZoneInfo.Local).Date),
            12, 7, 5, 72, 73, 812, 781,
            new[] { new PhaseChange(GamePhase.Laning, 690, 752), new PhaseChange(GamePhase.Mid, 840, 801), new PhaseChange(GamePhase.Late, 990, 1004) },
            new PhaseChange(GamePhase.Laning, 690, 752), new ItemChange(4053935515, new TimeSpan(0, 15, 9), new TimeSpan(0, 14, 21), 6),
            new WeeklyFocus(FocusKind.Phase, "Mid game: 801 souls a minute, 5% down on the week before.")), assets, demo: true);
        // That match's spending recap, from its soul readings (made up).
        state.SetRecap(1, "3k+ unspent for 6:20, longest 2:10 from 18:40 · lost 2.3k haunted souls in 3 deaths");
        // The previous match's report, as it appears once the API has processed a match.
        state.SetLastMatch(new PostMatchReport(1, 13, true, DateTimeOffset.Now.AddMinutes(-52), new TimeSpan(0, 34, 12), 8, 3, 11, 31240, 2, 6,
            new[]
            {
                new PaceCheckpoint(10, 6150, 5800, 0.06, 64), new PaceCheckpoint(20, 12210, 11600, 0.053, 61),
                new PaceCheckpoint(30, 17050, 17400, -0.02, 47),
            },
            new LaneResult(10, 6150, 4910, new[] { 4 }), new RankBracket(65, 85), "metadata", 40,
            new[]
            {
                new PhaseResult(GamePhase.Laning, TimeSpan.Zero, TimeSpan.FromMinutes(10), 6150, 5800),
                new PhaseResult(GamePhase.Mid, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), 6060, 6650),
                new PhaseResult(GamePhase.Late, TimeSpan.FromMinutes(20), new TimeSpan(0, 34, 12), 19030, 17900),
            }, "Oracle"), assets, demo: true);
        var me = players[0];
        foreach (var opponent in players.Where(p => p.Team != me.Team && p.Lane == me.Lane))
            state.SetMatchup(opponent.AccountId, new HeroMatchup(me.HeroId, opponent.HeroId, 2572, 1368));
        // Recent form for the lane opponent (Cinder on Lady Geist): made-up games, a hot streak above their usual pace.
        if (players.FirstOrDefault(p => p.Name == "Cinder") is { } laneOpponent)
        {
            state.SetForm(laneOpponent.AccountId, new RecentForm(10, 7, 3, 846, 776));
            // A matchup note on their hero, as you would write one in the match history; the demo's own, not settings.json's.
            state.SetDemoNotes(new Dictionary<int, string> { [laneOpponent.HeroId] = "Poke her early; her lifesteal wins long trades" });
        }
        // Counter items on three enemies: real item ids so names resolve; the lifts and game counts are made up. The rest of
        // the enemy team has no item clear enough, as most matchups do.
        const long inhibitor = 2037039379, frenzy = 339443430, healbane = 2603935618;
        var counterItems = new[] { (Name: "Brim", Item: inhibitor, Lift: 0.013, Games: 44180), (Name: "Ember", Item: frenzy, Lift: 0.033, Games: 5737),
                                   (Name: "Cinder", Item: healbane, Lift: 0.021, Games: 9120) };
        foreach (var (name, item, lift, games) in counterItems)
            if (players.FirstOrDefault(p => p.Name == name) is { } enemy)
                state.SetCounter(enemy.AccountId, new CounterItem(item, enemy.HeroId, lift, 4.0, games));
        state.Pace.Provider = "demo"; state.Pace.SampleSize = 40; state.Pace.Bracket = "all";
        // About 6% ahead of pace, swinging ±12% with fights and farm, so the sparkline has a shape.
        for (int s = 0; s <= 860; s += 30) state.Meter.Add(new SoulSample(TimeSpan.FromSeconds(s), (int)(s * 10.2 + 70 * Math.Sin(s / 360.0 * 2 * Math.PI)), SampleSource.LiveFeed));
        // Objectives: the urn appeared at 10:00 and is still out; the enemy took the mid boss at 11:20 and its Rejuvenator.
        foreach (var o in new[]
                 {
                     new ObjectiveSample(TimeSpan.FromMinutes(0), true, 0, 0, false), new ObjectiveSample(TimeSpan.FromMinutes(10), true, 0, 0, true),
                     new ObjectiveSample(new TimeSpan(0, 11, 20), false, 0, 0, true), new ObjectiveSample(new TimeSpan(0, 11, 24), false, 0, 1, true),
                 })
            state.ObjectiveTracker.Observe(o);
        // Power spikes: real item ids so names resolve; who holds what is made up. The first enemy's Leech showed up ten
        // seconds ago, so their row flashes; the others carry a quiet chip for an earlier spike, one of each colour
        // (weapon, ultimate, spirit).
        const long leech = 865846625, headhunter = 4053935515, mysticReverb = 3577481646;
        var spikes = new HashSet<long> { leech, headhunter, mysticReverb, ItemSpikes.UltimateId };
        var held = new[] { Array.Empty<long>(), new long[] { headhunter }, Array.Empty<long>(), new long[] { mysticReverb } };
        // Build tendencies, made up: the first enemy just got the Leech they usually open with, so Mystic Reverb is next;
        // the second holds Headhunter and goes Leech next; the third has bought none of theirs yet; the rest have no history.
        const long fortitude = 3585132399;
        static TendencyItem Usual(long id, int bought, int games, int min, int sec) => new(id, (double)bought / games, new TimeSpan(0, min, sec), games);
        var habits = new[]
        {
            new[] { Usual(leech, 8, 10, 17, 20), Usual(mysticReverb, 6, 10, 19, 40), Usual(fortitude, 5, 10, 24, 10) },
            new[] { Usual(headhunter, 9, 12, 12, 50), Usual(leech, 7, 12, 21, 30) },
            new[] { Usual(fortitude, 6, 9, 15, 30), Usual(mysticReverb, 5, 9, 22, 15) },
        };
        // Both teams' souls for the team lead line, in game-time order: Sapphire ends the 14 minutes about 2k ahead, after
        // Amber led early, so the line reads "Enemy +2.1k, growing".
        var teamLead = new TeamSoulsLead(players.Select(p => (p.AccountId, p.Team)));
        for (int s = 0; s <= 840; s += 30)
            foreach (var p in players)
            {
                int final = p.Team == Teams.Sapphire ? 8000 + (int)p.AccountId % 7 * 300 : 8500;
                double f = s / 840.0;
                teamLead.Observe(new NetWorthTick(TimeSpan.FromSeconds(s), p.AccountId, (int)(final * (p.Team == Teams.Sapphire ? f : Math.Pow(f, 0.9))), 0, 0, 0));
            }
        state.TeamLead = teamLead;
        int n = 0;
        foreach (var p in players.Where(p => p.Team == Teams.Sapphire))
        {
            // Their souls over the match from the broadcast, for the sparkline's enemy lines: a steady rate that lands on
            // the net worth below at 14:00, wobbling with fights; your lane opponent Cinder starts ahead and falls back.
            int final = 8000 + (int)p.AccountId % 7 * 300;
            for (int s = 0; s <= 810; s += 30)
            {
                double lead = p.Name == "Cinder" ? 450 * Math.Sin(Math.Min(s, 600) / 600.0 * Math.PI) : 0;
                int souls = (int)(final * s / 840.0 + lead + 60 * Math.Sin((s + p.AccountId * 50) / 240.0 * 2 * Math.PI));
                state.UpdateEnemy(new NetWorthTick(TimeSpan.FromSeconds(s), p.AccountId, Math.Max(0, souls), 0, 0, 0), p, assets);
            }
            state.EnemyCurves[p.AccountId] = curve;
            state.EnemySpikes[p.AccountId] = spikes;
            if (n < habits.Length) state.SetTendencies(p.AccountId, habits[n]);
            int nw = 8000 + (int)p.AccountId % 7 * 300;
            var items = n < held.Length ? held[n] : Array.Empty<long>();
            state.UpdateEnemy(new NetWorthTick(TimeSpan.FromSeconds(840), p.AccountId, nw, 2, 1, 3, items, n == 2), p, assets);
            if (n == 0) items = items.Append(leech).ToArray();
            state.UpdateEnemy(new NetWorthTick(TimeSpan.FromSeconds(850), p.AccountId, nw, 2, 1, 3, items, n == 2), p, assets);
            n++;
        }
        state.Refresh();
    }
}
