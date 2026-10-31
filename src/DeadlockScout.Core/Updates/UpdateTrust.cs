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
    /// from it, so either can sign the update that retires the other (`ReleaseSign keygen`, see docs/release.md).
    /// Changing this list is changing who can ship updates: only ever in a reviewed pull request, and the fingerprints
    /// in docs/release.md and the test must change with it.
    /// </summary>
    public static readonly IReadOnlyList<string> TrustedKeys = new[]
    {
        // signing, fingerprint E91B FCE3 DFE9 7A88 93CA F816 0D8C 7016 9B28 FECC 6189 11EB 8C35 33D0 5D9D 79B1
        """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEk2zY7jNzvtqNdizXzd0ebf8iOH7b
        0Wjck/nt/SSTCFGpQ7OUPXnbMnDl+T+R6f3I692DwGa4Q8uVvjgIX3FT3g==
        -----END PUBLIC KEY-----
        """,
        // backup (offline), fingerprint 7C4C 4599 300F 36C3 B711 EB5A 61F9 4598 8314 AB50 646B 6527 1A01 13B6 8B84 C492
        """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBzUhMBUkqEFpYnzNVF9xZsq6MVTb
        8EYPhToRxjFr8YuKqgGG5HQI4we/mJTJKL0Rvv/gcrJbxK1QTg67JJyoQA==
        -----END PUBLIC KEY-----
        """,
    };

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
