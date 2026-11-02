using System.Security.Cryptography;
using System.Text;

namespace DeadlockScout.App.Services;

/// <summary>
/// Windows DPAPI for the current user: what it encrypts only this Windows account on this PC can decrypt, so the API key
/// in settings.json is useless in a copied or shared file. Not a defence against software running as the same user.
/// </summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeadlockScout.ApiKey");

    public static string Protect(string plain)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>The plain text, or null when the value is not ours (another account or PC, or damaged).</summary>
    public static string? Unprotect(string base64)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}
