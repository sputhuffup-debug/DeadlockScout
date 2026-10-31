using System.Security.Cryptography;
using System.Text;

namespace DeadlockScout.Core.Updates;

/// <summary>
/// Checks a release against the keys built into this copy of the app, so a release is only as trusted as the owner's
/// offline signing key, never as GitHub. Someone who takes over the repository, its releases or its Actions can upload
/// any installer, but cannot sign a manifest for it; an installed app refuses it. ECDSA P-256 over SHA-256, signatures
/// in DER, the key in SubjectPublicKeyInfo PEM; all built into .NET, nothing to download.
/// </summary>
public static class UpdateTrust
{
    /// <summary>
    /// The public keys a release manifest may be signed with: the owner's signing key and an offline backup kept apart
    /// from it, so either can sign the update that retires the other. Empty until the owner generates them
    /// (`ReleaseSign keygen`, see docs/release.md); with no key here the updater offers nothing.
    /// </summary>
    public static readonly IReadOnlyList<string> TrustedKeys = Array.Empty<string>();

    /// <summary>Whether <paramref name="signature"/> is a valid signature of <paramref name="manifest"/> by any of <paramref name="keys"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature, IEnumerable<string> keys)
    {
        foreach (var pem in keys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(pem);
                if (key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
            }
            catch (CryptographicException) { } // a malformed key or signature is a failed check, never a crash
            catch (ArgumentException) { }
        }
        return false;
    }

    /// <summary>The signature file's text (base64 of the DER signature), or null when it is not base64.</summary>
    public static byte[]? ReadSignature(string text)
    {
        try { return Convert.FromBase64String(text.Trim()); }
        catch (FormatException) { return null; }
    }

    /// <summary>Lower-case hex SHA-256 of a stream, read to its end.</summary>
    public static string Sha256(Stream content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>"3F2A 9C11 ..." : the SHA-256 of a public key's DER, for checking a key against one published off GitHub.</summary>
    public static string Fingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var hex = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 4) sb.Append(i > 0 ? " " : "").Append(hex, i, 4);
        return sb.ToString();
    }
}
