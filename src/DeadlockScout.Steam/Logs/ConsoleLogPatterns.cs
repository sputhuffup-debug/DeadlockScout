using System.Text.RegularExpressions;

namespace DeadlockScout.Steam.Logs;

/// <summary>
/// Every regex that reads the game's console.log, in one place, so a patch that changes a line is a one-file fix.
/// Each pattern names what it needs in groups. Lines are matched after the "MM/DD HH:MM:SS" prefix is stripped.
/// The line shapes come from a real -condebug log (Oct 27, 2026: hideout, two bot matches, a matchmade match played to
/// the end screen); the scrubbed lines are in tests/Fixtures/console/real-match.log.
/// </summary>
public static partial class ConsoleLogPatterns
{
    /// <summary>"Lobby 2998877000000001 for Match 41223344 created". Groups: lobby, match.</summary>
    [GeneratedRegex(@"Lobby\s+(?<lobby>\d+)\s+for\s+Match\s+(?<match>\d+)\s+created", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex LobbyCreated();

    /// <summary>
    /// "Lobby 2998877000000001 for Match 41223344 destroyed", a second after the end screen opens. Not logged when you
    /// leave a match early (the lobby stays open for a reconnect). Groups: lobby, match.
    /// </summary>
    [GeneratedRegex(@"Lobby\s+(?<lobby>\d+)\s+for\s+Match\s+(?<match>\d+)\s+destroyed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex LobbyDestroyed();

    /// <summary>
    /// "[SteamNetSockets] Received Steam datagram ticket for server steamid:900... vport -1. match_id=41223344",
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
    /// the game in progress; the end screen is "PostGame (8)", then "End (11)" as the client leaves. Groups: name, state.
    /// </summary>
    [GeneratedRegex(@"^OnGameStateChanged:\s+(?<name>\w+)\s+\((?<state>\d+)\)", RegexOptions.CultureInvariant)]
    public static partial Regex GameState();

    /// <summary>Game states at or above this mean the match is over: PostGame (8) opens the end screen.</summary>
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
