using System.Text;
using System.Text.RegularExpressions;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Steam;

/// <summary>
/// Reads replay salts out of the Steam client's HTTP cache after you open a match in-game, so they can be sent to the
/// ingest endpoint with no API reads. Scanner logic mirrors the deadlock-api website's cache uploader: look for
/// "replayN.valve.net/1422450/{match}_{salt}.(meta|dem).bz2" byte sequences. Full match metadata from the cache is still
/// an open question (format not captured), so <see cref="TryReadMatch"/> returns null and callers use the API.
/// </summary>
public sealed partial class SteamHttpCache
{
    private readonly string _root;

    public SteamHttpCache(string steamPath) => _root = SteamLocator.HttpCachePath(steamPath);

    public bool Exists => Directory.Exists(_root);

    [GeneratedRegex(@"http://replay(?<cluster>\d+)\.valve\.net/1422450/(?<match>\d+)_(?<salt>\d+)\.(?<type>meta|dem)\.bz2", RegexOptions.CultureInvariant)]
    private static partial Regex ReplayUrl();

    /// <summary>Files touched since <paramref name="since"/>, newest first.</summary>
    public IEnumerable<FileInfo> RecentEntries(DateTimeOffset since)
    {
        if (!Exists) yield break;
        var files = new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTimeUtc >= since.UtcDateTime)
            .OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var f in files) yield return f;
    }

    /// <summary>Salts for every Deadlock replay URL in cache files touched since <paramref name="since"/>.</summary>
    public IReadOnlyList<MatchSalts> ScanSalts(DateTimeOffset since, long maxFileBytes = 64 * 1024 * 1024)
    {
        var found = new Dictionary<long, MatchSalts>();
        foreach (var file in RecentEntries(since))
        {
            if (file.Length > maxFileBytes) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file.FullName); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var s in ExtractSalts(bytes))
                found[s.MatchId] = found.TryGetValue(s.MatchId, out var prev) ? Combine(prev, s) : s;
        }
        return found.Values.OrderByDescending(s => s.MatchId).ToList();
    }

    /// <summary>Pure extraction from raw bytes, for tests.</summary>
    public static IReadOnlyList<MatchSalts> ExtractSalts(ReadOnlySpan<byte> bytes)
    {
        // Latin-1 keeps one char per byte so regex offsets are not disturbed by binary noise.
        var text = Encoding.Latin1.GetString(bytes);
        var result = new Dictionary<long, MatchSalts>();
        foreach (Match m in ReplayUrl().Matches(text))
        {
            long match = long.Parse(m.Groups["match"].Value), salt = long.Parse(m.Groups["salt"].Value), cluster = long.Parse(m.Groups["cluster"].Value);
            var s = m.Groups["type"].Value == "meta"
                ? new MatchSalts(match, cluster, salt, null)
                : new MatchSalts(match, cluster, null, salt);
            result[match] = result.TryGetValue(match, out var prev) ? Combine(prev, s) : s;
        }
        return result.Values.ToList();
    }

    private static MatchSalts Combine(MatchSalts a, MatchSalts b)
        => new(a.MatchId, a.ClusterId ?? b.ClusterId, a.MetadataSalt ?? b.MetadataSalt, a.ReplaySalt ?? b.ReplaySalt);

    /// <summary>Not implemented until the cache format is captured; always null so callers fall back to the API.</summary>
    public MatchMetadata? TryReadMatch(long matchId) => null;
}
