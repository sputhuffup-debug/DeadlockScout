namespace DeadlockScout.Core.Settings;

/// <summary>
/// Keeps the deadlock-api key out of settings.json in plain text. In memory the settings carry the plain key
/// (<see cref="ApiSettings.ApiKey"/>) for the API key handler; on disk only the encrypted one
/// (<see cref="ApiSettings.ApiKeyProtected"/>), so a copied or shared settings file does not carry a usable key. The
/// encryption is passed in (Windows DPAPI for the current user in the app), so the rules are tested anywhere.
/// </summary>
public static class SettingsSecrets
{
    public const string Prefix = "dpapi1:";

    /// <summary>What a load found about the key.</summary>
    public enum KeyState
    {
        /// <summary>No key, or an encrypted one that decrypted.</summary>
        Fine,
        /// <summary>A plain key in the file (an older version, or typed by hand): the file should be written again, encrypted.</summary>
        PlainOnDisk,
        /// <summary>An encrypted key that does not decrypt here: the file came from another PC or Windows account.</summary>
        Unreadable,
    }

    /// <summary>
    /// The settings as read from disk, made ready for use: the encrypted key decrypted into <see cref="ApiSettings.ApiKey"/>.
    /// A plain key wins over an encrypted one (it is what the player typed last).
    /// </summary>
    /// <param name="unprotect">Decrypts the base64 after the prefix; null when it cannot.</param>
    public static ScoutSettings FromDisk(ScoutSettings s, Func<string, string?> unprotect, out KeyState state)
    {
        state = KeyState.Fine;
        var api = s.Api;
        if (!string.IsNullOrWhiteSpace(api.ApiKey))
        {
            state = KeyState.PlainOnDisk;
            return s with { Api = api with { ApiKey = api.ApiKey.Trim(), ApiKeyProtected = null } };
        }
        if (string.IsNullOrWhiteSpace(api.ApiKeyProtected)) return s with { Api = api with { ApiKey = null, ApiKeyProtected = null } };
        string? key = null;
        if (api.ApiKeyProtected.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try { key = unprotect(api.ApiKeyProtected[Prefix.Length..]); } catch { key = null; }
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            state = KeyState.Unreadable;
            return s with { Api = api with { ApiKey = null } }; // the encrypted value stays until a new key is entered
        }
        return s with { Api = api with { ApiKey = key } };
    }

    /// <summary>The settings as written to disk: the plain key replaced by its encrypted form.</summary>
    /// <param name="protect">Encrypts the key into base64.</param>
    public static ScoutSettings ForDisk(ScoutSettings s, Func<string, string> protect)
    {
        var api = s.Api;
        if (string.IsNullOrWhiteSpace(api.ApiKey)) return s with { Api = api with { ApiKey = null } }; // keeps an unreadable one as it was
        return s with { Api = api with { ApiKey = null, ApiKeyProtected = Prefix + protect(api.ApiKey) } };
    }

    /// <summary>The warning shown when the key could not be read.</summary>
    public const string UnreadableWarning =
        "The deadlock-api key in settings.json was saved by another Windows account or PC, so it cannot be read here. Enter it again in Settings → Live feed and API.";
}
