using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadlockScout.Core.Updates;

/// <summary>
/// What one release claims about itself, published next to its installer as <c>release-manifest.json</c> and signed
/// with the owner's offline key (<c>release-manifest.json.sig</c>). The updater trusts nothing about a release that is
/// not in a manifest whose signature verifies: not the version on the GitHub release, not the file it links.
/// </summary>
public sealed record ReleaseManifest(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("published")] DateTimeOffset Published,
    /// <summary>After this the manifest is refused, so an old one cannot be replayed to hold players on an old release.</summary>
    [property: JsonPropertyName("expires")] DateTimeOffset Expires,
    [property: JsonPropertyName("installer")] string Installer,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("notes")] string? Notes = null)
{
    public const string ProductName = "DeadlockScout";
    public const string FileName = "release-manifest.json";
    public const string SignatureFileName = "release-manifest.json.sig";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The manifest's bytes as signed. The signature covers these exact bytes, never a re-serialised copy.</summary>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>The manifest in <paramref name="bytes"/>, or null when it is not one (missing fields, bad JSON).</summary>
    public static ReleaseManifest? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ReleaseManifest>(bytes, Json);
            return m is { Product.Length: > 0, Version.Length: > 0, Installer.Length: > 0, Sha256.Length: 64 } && m.Size > 0 ? m : null;
        }
        catch (JsonException) { return null; }
    }
}
