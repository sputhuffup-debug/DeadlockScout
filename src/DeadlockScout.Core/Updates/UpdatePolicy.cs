namespace DeadlockScout.Core.Updates;

public enum UpdateVerdict
{
    /// <summary>A newer, signed, current release: the tray may offer it (between matches).</summary>
    Offer,
    /// <summary>Signed and current, but not newer than what runs: nothing to do.</summary>
    UpToDate,
    /// <summary>No trusted key in this build, or the signature does not verify against any: refused.</summary>
    Untrusted,
    /// <summary>The manifest is unreadable, or names another product: refused.</summary>
    Invalid,
    /// <summary>Past its expiry: refused, so an old manifest cannot be replayed to hold players on an old release.</summary>
    Expired,
    /// <summary>Older than what runs: refused, so nobody can push players back to a release with a known bug.</summary>
    Downgrade,
}

/// <summary>
/// Whether a release may be offered: signed by a trusted key, about this product, not expired, and newer than the
/// running version. Pure, so every refusal is tested. The installer's own hash is checked after the download
/// (<see cref="InstallerMatches"/>), before anything runs.
/// </summary>
public static class UpdatePolicy
{
    public static (UpdateVerdict Verdict, ReleaseManifest? Manifest) Evaluate(
        ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature, IReadOnlyCollection<string> trustedKeys, Version running, DateTimeOffset now)
    {
        if (trustedKeys.Count == 0 || !UpdateTrust.Verify(manifestBytes, signature, trustedKeys)) return (UpdateVerdict.Untrusted, null);
        var m = ReleaseManifest.Parse(manifestBytes);
        if (m is null || m.Product != ReleaseManifest.ProductName || !System.Version.TryParse(m.Version, out var version)) return (UpdateVerdict.Invalid, null);
        if (now >= m.Expires) return (UpdateVerdict.Expired, m);
        int cmp = Normalize(version).CompareTo(Normalize(running));
        if (cmp < 0) return (UpdateVerdict.Downgrade, m);
        return cmp == 0 ? (UpdateVerdict.UpToDate, m) : (UpdateVerdict.Offer, m);
    }

    /// <summary>Whether the downloaded installer is the one the signed manifest names: size, then SHA-256.</summary>
    public static bool InstallerMatches(ReleaseManifest manifest, Stream installer)
    {
        if (installer.CanSeek && installer.Length != manifest.Size) return false;
        return string.Equals(UpdateTrust.Sha256(installer), manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>1.0 and 1.0.0 compare equal: missing parts count as 0.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
