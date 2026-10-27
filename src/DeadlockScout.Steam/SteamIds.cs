namespace DeadlockScout.Steam;

/// <summary>SteamID64 ⇄ SteamID3 account id. The API uses the 32-bit account id everywhere.</summary>
public static class SteamIds
{
    public const ulong Steam64Base = 76561197960265728UL;

    public static uint ToAccountId(ulong steamId64) => (uint)(steamId64 - Steam64Base);
    public static ulong ToSteamId64(uint accountId) => Steam64Base + accountId;

    public static bool TryParseAny(string text, out uint accountId)
    {
        accountId = 0;
        if (ulong.TryParse(text, out var n))
        {
            if (n >= Steam64Base) { accountId = ToAccountId(n); return true; }
            if (n <= uint.MaxValue) { accountId = (uint)n; return true; }
        }
        // [U:1:12345]
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\[U:1:(\d+)\]");
        if (m.Success && uint.TryParse(m.Groups[1].Value, out accountId)) return true;
        return false;
    }
}
