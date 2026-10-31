namespace DeadlockScout.Core.Updates;

/// <summary>
/// What the tray's "Check for updates" tells the player: a title and a line for a balloon, for each way a check the
/// player asked for can end. Pure, so every outcome is tested.
/// </summary>
public static class ManualCheck
{
    public static readonly (string Title, string Text) NotInstalled =
        ("Updates", "This copy runs from a build folder; only an installed copy updates itself.");

    public static readonly (string Title, string Text) DuringMatch =
        ("Updates", "Updates are checked between matches. Try again after this one.");

    /// <summary>
    /// The outcome of a finished check. <paramref name="verdict"/> is null when GitHub could not be reached;
    /// <paramref name="latest"/> is the latest release's version when its manifest was read.
    /// </summary>
    public static (string Title, string Text) Describe(UpdateVerdict? verdict, string running, string? latest) => verdict switch
    {
        null => ("Couldn't check for updates", "GitHub could not be reached. Try again later."),
        UpdateVerdict.Offer => ($"Update {latest} available",
            $"Install it with \"Update to {latest}\" at the top of the tray menu, between matches."),
        UpdateVerdict.UpToDate => ("Up to date", $"DeadlockScout {running} is the latest version."),
        UpdateVerdict.Downgrade => ("Up to date", $"DeadlockScout {running} is newer than the latest release ({latest})."),
        UpdateVerdict.Expired => ("No update installed",
            $"The latest release ({latest}) has an expired signature, so it is ignored until it is signed again."),
        _ => ("No update installed",
            "The latest release on GitHub is not signed with DeadlockScout's key, so it is ignored. Nothing was installed."),
    };
}
