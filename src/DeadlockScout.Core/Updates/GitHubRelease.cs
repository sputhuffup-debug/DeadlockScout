using System.Text.Json;

namespace DeadlockScout.Core.Updates;

/// <summary>One file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>
/// The parts of GitHub's "latest release" answer the updater reads: its tag and its files. Nothing here is trusted;
/// it only says where to fetch the signed manifest and, once that verifies, the installer it names.
/// </summary>
public sealed record GitHubRelease(string Tag, IReadOnlyList<ReleaseAsset> Assets)
{
    public const string LatestUrl = "https://api.github.com/repos/sputhuffup-debug/DeadlockScout/releases/latest";

    /// <summary>The largest manifest or signature fetched; real ones are under 1 KB.</summary>
    public const long MaxSmallFile = 64 * 1024;

    /// <summary>The hosts a release file may come from (the API, the release page, and where its downloads redirect).</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    /// <summary>HTTPS from one of <see cref="AllowedHosts"/>; anything else is never fetched.</summary>
    public static bool Allowed(Uri? url) => url is { Scheme: "https" } && AllowedHosts.Contains(url.Host);

    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>The release in GitHub's JSON, or null when it is a draft, a prerelease or not a release at all.</summary>
    public static GitHubRelease? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    if (!a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    if (!a.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String) continue;
                    if (!Uri.TryCreate(u.GetString(), UriKind.Absolute, out var url) || !Allowed(url)) continue;
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                    assets.Add(new ReleaseAsset(n.GetString()!, url, size));
                }
            return new GitHubRelease(tag.GetString()!, assets);
        }
        catch (JsonException) { return null; }
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
