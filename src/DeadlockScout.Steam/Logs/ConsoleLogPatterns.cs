using System.Text.RegularExpressions;

namespace DeadlockScout.Steam.Logs;

/// <summary>
/// Every regex that reads the game's console.log, in one place, so a patch that changes a line is a one-file fix.
/// Each pattern names what it needs in groups. Lines are matched after the "MM/DD HH:MM:SS" prefix is stripped.
/// The line shapes come from a real -condebug log (Oct 27, 2026: hideout, two bot matches, a matchmade match); the
/// scrubbed lines are in tests/Fixtures/console/real-match.log. Patterns marked UNVERIFIED are still guesses.
/// </summary>
public static partial class ConsoleLogPatterns
{
    /// <summary>"Lobby 102141584199393730 for Match 126188769 created". Groups: lobby, match.</summary>
    [GeneratedRegex(@"Lobby\s+(?<lobby>\d+)\s+for\s+Match\s+(?<match>\d+)\s+created", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex LobbyCreated();

    /// <summary>
    /// "[SteamNetSockets] Received Steam datagram ticket for server steamid:902... vport -1. match_id=126188769",
    /// logged just before the lobby line when the game server is assigned. Groups: match.
    /// </summary>
    [GeneratedRegex(@"datagram ticket for server\b.*\bmatch_id=(?<match>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex ServerTicket();

    /// <summary>
    /// "Attempting to spawn hero hero_frank (66). GameState is 7". Seen for the local player in a bot match; a
    /// matchmade match did not log it, so the hero normally comes from the live roster. Groups: hero.
    /// </summary>
    [GeneratedRegex(@"^Attempting to spawn hero\s+(?<hero>hero_[a-z0-9_]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex HeroSpawn();

    /// <summary>
    /// The client's game state: "OnGameStateChanged: GameInProgress (7)". States 2–7 run from waiting for players to
    /// the game in progress. Groups: name, state. The post-game states (8 and up) are UNVERIFIED: the captured log
    /// ended mid-match, so <see cref="FirstPostGameState"/> is taken from the order of the game's own state enum.
    /// </summary>
    [GeneratedRegex(@"^OnGameStateChanged:\s+(?<name>\w+)\s+\((?<state>\d+)\)", RegexOptions.CultureInvariant)]
    public static partial Regex GameState();

    /// <summary>Game states at or above this mean the match is over (post-game, abandoned, end). UNVERIFIED.</summary>
    public const int FirstPostGameState = 8;

    /// <summary>
    /// "[Client] Disconnecting from server: NETWORK_DISCONNECT_LOOPDEACTIVATE" and "[Client] Disconnected from
    /// server: NETWORK_DISCONNECT_DISCONNECT_BY_USER". Groups: reason. Only the client's own line; the local listen
    /// server's "[Server] Disconnect client ..." lines are ignored.
    /// </summary>
    [GeneratedRegex(@"^\[Client\]\s+Disconnect(?:ed|ing)\s+from\s+server:\s+(?<reason>NETWORK_DISCONNECT_\w+)", RegexOptions.CultureInvariant)]
    public static partial Regex Disconnected();

    /// <summary>
    /// The disconnect the engine logs on every map or loop switch, including hideout to match server right after the
    /// lobby line. It never means the match ended.
    /// </summary>
    public const string LoopSwitchReason = "NETWORK_DISCONNECT_LOOPDEACTIVATE";

    /// <summary>
    /// "Loading map "dl_hideout"": the game going back to the hideout, after leaving a match or when it ends. Also
    /// logged at start-up, before any match.
    /// </summary>
    [GeneratedRegex(@"^Loading map\s+""dl_hideout""", RegexOptions.CultureInvariant)]
    public static partial Regex HideoutLoading();

    /// <summary>Leading timestamp "MM/DD HH:MM:SS" (current builds) or "[HH:MM:SS]".</summary>
    [GeneratedRegex(@"^\s*(?:\[?\d{1,2}[:/]\d{2}(?:[:/]\d{2,4})?(?:\s+\d{1,2}:\d{2}:\d{2})?\]?\s*)", RegexOptions.CultureInvariant)]
    public static partial Regex LeadingTimestamp();

    public static string StripTimestamp(string line) => LeadingTimestamp().Replace(line, "", 1).Trim();
}
