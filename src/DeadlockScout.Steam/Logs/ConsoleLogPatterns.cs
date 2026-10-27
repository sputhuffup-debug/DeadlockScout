using System.Text.RegularExpressions;

namespace DeadlockScout.Steam.Logs;

/// <summary>
/// Every regex that reads the game's console.log, in one place, so a patch that changes a line is a one-file fix.
/// Each pattern names what it needs in groups. Lines are matched with trailing/leading whitespace trimmed.
/// Patterns marked UNVERIFIED were written from the design doc and community reports, not from a captured log on the
/// current build; replace them from a real fixture (tests/Fixtures/console/*.log) when one is collected.
/// </summary>
public static partial class ConsoleLogPatterns
{
    /// <summary>"Lobby 29876543 for Match 38765432 created". Groups: lobby, match.</summary>
    [GeneratedRegex(@"Lobby\s+(?<lobby>\d+)\s+for\s+Match\s+(?<match>\d+)\s+created", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex LobbyCreated();

    /// <summary>Hero lock / selection line. UNVERIFIED. Groups: hero (internal name like hero_haze).</summary>
    [GeneratedRegex(@"(?:selected|locked|assigned|chose)\s+hero\W+(?<hero>hero_[a-z0-9_]+)|\bhero[_\s]?(?:id|name)\W+(?<hero>hero_[a-z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex HeroLocked();

    /// <summary>The local player's account on connect. UNVERIFIED. Groups: account (SteamID3 number or [U:1:n]).</summary>
    [GeneratedRegex(@"(?:SteamID|steamid|account[_ ]?id)\W+(?<account>\[U:1:\d+\]|\d{5,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex AccountSeen();

    /// <summary>Match finished / end screen. UNVERIFIED. Groups: match (optional).</summary>
    [GeneratedRegex(@"(?:Match\s+(?<match>\d+)\s+(?:ended|finished|complete)|\bpost[_ ]?game\b|\bgame[_ ]?over\b|\bmatch[_ ]?end(?:ed)?\b|CCitadelGameRules.*(?:GameOver|PostGame))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex MatchEnded();

    /// <summary>Disconnect from the game server, which also ends a match for us. UNVERIFIED.</summary>
    [GeneratedRegex(@"^(?:\[Client\]\s*)?(?:Disconnect(?:ed|ing)?\s+from\s+server|CL:\s+Disconnect|Host_Shutdown|Shutting down game server connection)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex Disconnected();

    /// <summary>Matchmaking state changes, for the status line. UNVERIFIED. Groups: state.</summary>
    [GeneratedRegex(@"matchmaking\s+state\W+(?<state>[A-Za-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex MatchmakingState();

    /// <summary>Optional leading timestamp "MM/DD HH:MM:SS" or "[HH:MM:SS]" that some builds prepend.</summary>
    [GeneratedRegex(@"^\s*(?:\[?\d{1,2}[:/]\d{2}(?:[:/]\d{2,4})?(?:\s+\d{1,2}:\d{2}:\d{2})?\]?\s*)", RegexOptions.CultureInvariant)]
    public static partial Regex LeadingTimestamp();

    public static string StripTimestamp(string line) => LeadingTimestamp().Replace(line, "", 1).Trim();
}
