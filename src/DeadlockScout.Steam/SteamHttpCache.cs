using DeadlockScout.Core.Models;

namespace DeadlockScout.Steam;

/// <summary>
/// Reads match metadata the Steam client already downloaded (after you open a match in-game) so the post-match report
/// and the personal benchmark cost no API calls. The cache layout on the current client is an open question in the
/// design doc; until a sample is captured this only enumerates candidate files, and <see cref="TryReadMatch"/> returns null.
/// </summary>
public sealed class SteamHttpCache
{
    private readonly string _root;

    public SteamHttpCache(string steamPath) => _root = SteamLocator.HttpCachePath(steamPath);

    public bool Exists => Directory.Exists(_root);

    /// <summary>Files touched since <paramref name="since"/>, newest first: the candidates for a just-finished match.</summary>
    public IEnumerable<FileInfo> RecentEntries(DateTimeOffset since)
    {
        if (!Exists) yield break;
        var files = new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTimeUtc >= since.UtcDateTime)
            .OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var f in files) yield return f;
    }

    /// <summary>Not implemented until the cache format is captured; always null so callers fall back to the API.</summary>
    public MatchMetadata? TryReadMatch(long matchId) => null;

    public MatchSalts? TryReadSalts(long matchId) => null;
}
