namespace DeadlockScout.Core.Updates;

/// <summary>
/// After an update, the first start of the new version says so once, with a link to that release's notes on GitHub.
/// The version that ran last is kept in settings (<see cref="Settings.UpdateSettings.LastRunVersion"/>); a first install
/// (nothing kept yet) says nothing, and neither does going back to an older build. Pure, so tested.
/// </summary>
public static class WhatsNew
{
    public const string ReleasesPage = "https://github.com/sputhuffup-debug/DeadlockScout/releases/tag/v";

    /// <summary>The release notes' address when <paramref name="running"/> is newer than <paramref name="lastRun"/>; null otherwise.</summary>
    public static string? NotesFor(string? lastRun, Version running)
    {
        if (string.IsNullOrWhiteSpace(lastRun) || !Version.TryParse(lastRun, out var last)) return null;
        var now = Normalize(running);
        return now > Normalize(last) ? ReleasesPage + now.ToString(3) : null;
    }

    /// <summary>The version to keep as the last one run: major.minor.build.</summary>
    public static string Keep(Version running) => Normalize(running).ToString(3);

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
