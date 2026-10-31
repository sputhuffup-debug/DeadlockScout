using System.Security.Cryptography;
using System.Text;
using DeadlockScout.Core.Updates;

namespace DeadlockScout.Tests.Core;

/// <summary>A key made for each test run; the real signing key never exists outside the owner's PC.</summary>
public class UpdateTrustTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Version Running = new(1, 0, 0);

    private static (ECDsa Key, string PublicPem) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, key.ExportSubjectPublicKeyInfoPem());
    }

    private static ReleaseManifest Manifest(string version = "1.0.1", int expiresInDays = 90, string product = ReleaseManifest.ProductName)
        => new(product, version, Now.AddDays(-1), Now.AddDays(expiresInDays), $"DeadlockScout-Setup-{version}.exe",
            new string('a', 64), 1234, "https://example.invalid/notes");

    private static byte[] Sign(ECDsa key, byte[] bytes) => key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    [Fact]
    public void A_newer_release_signed_by_a_trusted_key_is_offered()
    {
        var (key, pub) = NewKey();
        var bytes = Manifest().ToBytes();
        var (verdict, m) = UpdatePolicy.Evaluate(bytes, Sign(key, bytes), new[] { pub }, Running, Now);
        Assert.Equal(UpdateVerdict.Offer, verdict);
        Assert.Equal("1.0.1", m!.Version);
    }

    [Fact]
    public void Anything_not_signed_by_a_trusted_key_is_refused()
    {
        var (key, pub) = NewKey();
        var (other, _) = NewKey();
        var bytes = Manifest().ToBytes();
        // Signed by someone else (what an attacker holding the GitHub account could do at best).
        Assert.Equal(UpdateVerdict.Untrusted, UpdatePolicy.Evaluate(bytes, Sign(other, bytes), new[] { pub }, Running, Now).Verdict);
        // Signed properly, then edited (another installer, another hash).
        var good = Sign(key, bytes);
        var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(new string('a', 64), new string('b', 64)));
        Assert.Equal(UpdateVerdict.Untrusted, UpdatePolicy.Evaluate(edited, good, new[] { pub }, Running, Now).Verdict);
        // A build with no key pinned offers nothing at all.
        Assert.Equal(UpdateVerdict.Untrusted, UpdatePolicy.Evaluate(bytes, good, Array.Empty<string>(), Running, Now).Verdict);
        // Garbage signature or key: a refusal, never an exception.
        Assert.Equal(UpdateVerdict.Untrusted, UpdatePolicy.Evaluate(bytes, new byte[] { 1, 2, 3 }, new[] { pub, "not a key" }, Running, Now).Verdict);
    }

    [Fact]
    public void The_backup_key_signs_as_well_as_the_main_one()
    {
        var (main, mainPub) = NewKey();
        var (backup, backupPub) = NewKey();
        var bytes = Manifest().ToBytes();
        Assert.Equal(UpdateVerdict.Offer, UpdatePolicy.Evaluate(bytes, Sign(backup, bytes), new[] { mainPub, backupPub }, Running, Now).Verdict);
        Assert.Equal(UpdateVerdict.Offer, UpdatePolicy.Evaluate(bytes, Sign(main, bytes), new[] { mainPub, backupPub }, Running, Now).Verdict);
    }

    [Theory]
    [InlineData("1.0.0", UpdateVerdict.UpToDate)]
    [InlineData("1.0", UpdateVerdict.UpToDate)]     // 1.0 is 1.0.0
    [InlineData("0.9.5", UpdateVerdict.Downgrade)]  // never back to an older release
    [InlineData("1.2.0", UpdateVerdict.Offer)]
    public void Only_a_newer_version_is_offered(string version, UpdateVerdict expected)
    {
        var (key, pub) = NewKey();
        var bytes = Manifest(version).ToBytes();
        Assert.Equal(expected, UpdatePolicy.Evaluate(bytes, Sign(key, bytes), new[] { pub }, Running, Now).Verdict);
    }

    [Fact]
    public void An_expired_or_foreign_manifest_is_refused_even_when_signed()
    {
        var (key, pub) = NewKey();
        var expired = Manifest(expiresInDays: 0).ToBytes();
        Assert.Equal(UpdateVerdict.Expired, UpdatePolicy.Evaluate(expired, Sign(key, expired), new[] { pub }, Running, Now).Verdict);
        var other = Manifest(product: "SomethingElse").ToBytes();
        Assert.Equal(UpdateVerdict.Invalid, UpdatePolicy.Evaluate(other, Sign(key, other), new[] { pub }, Running, Now).Verdict);
        var junk = Encoding.UTF8.GetBytes("{\"product\":\"DeadlockScout\"}");
        Assert.Equal(UpdateVerdict.Invalid, UpdatePolicy.Evaluate(junk, Sign(key, junk), new[] { pub }, Running, Now).Verdict);
    }

    [Fact]
    public void The_installer_must_match_the_signed_size_and_hash()
    {
        var content = Encoding.UTF8.GetBytes("installer bytes");
        var m = Manifest() with { Size = content.Length, Sha256 = UpdateTrust.Sha256(new MemoryStream(content)) };
        Assert.True(UpdatePolicy.InstallerMatches(m, new MemoryStream(content)));
        Assert.False(UpdatePolicy.InstallerMatches(m, new MemoryStream(Encoding.UTF8.GetBytes("installer bytez"))));  // same size, other bytes
        Assert.False(UpdatePolicy.InstallerMatches(m, new MemoryStream(Encoding.UTF8.GetBytes("installer bytes!")))); // other size
    }

    [Fact]
    public void Signatures_round_trip_through_their_text_file_and_keys_have_a_readable_fingerprint()
    {
        var (key, pub) = NewKey();
        var bytes = Manifest().ToBytes();
        var text = Convert.ToBase64String(Sign(key, bytes)) + "\n";
        Assert.True(UpdateTrust.Verify(bytes, UpdateTrust.ReadSignature(text)!, new[] { pub }));
        Assert.Null(UpdateTrust.ReadSignature("not base64!"));
        Assert.Matches(@"^([0-9A-F]{4} ){15}[0-9A-F]{4}$", UpdateTrust.Fingerprint(pub));
        Assert.Empty(UpdateTrust.TrustedKeys); // no key is pinned until the owner generates one
    }
}
