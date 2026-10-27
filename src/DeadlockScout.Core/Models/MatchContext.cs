namespace DeadlockScout.Core.Models;

/// <summary>Everything the app knows about the current match at the moment it was detected.</summary>
/// <param name="MatchId">Valve match id from the console log, or 0 when only the Steam content log fired.</param>
/// <param name="LobbyId">Lobby id from the console log, or 0 when unknown.</param>
/// <param name="MyAccountId">The local player's SteamID3 account id.</param>
/// <param name="DetectedAt">Wall-clock time the detector fired.</param>
public sealed record MatchContext(long MatchId, long LobbyId, uint MyAccountId, DateTimeOffset DetectedAt)
{
    /// <summary>True when a real match id is known (console log), false for the content-log fallback.</summary>
    public bool HasMatchId => MatchId > 0;
}
