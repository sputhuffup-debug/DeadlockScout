using DeadlockScout.Core.Updates;

namespace DeadlockScout.Tests.Core;

public class ManualCheckTests
{
    [Fact]
    public void An_offer_points_to_the_tray_item()
    {
        var (title, text) = ManualCheck.Describe(UpdateVerdict.Offer, "0.1.2", "0.1.3");
        Assert.Equal("Update 0.1.3 available", title);
        Assert.Contains("\"Update to 0.1.3\"", text);
        Assert.Contains("between matches", text);
    }

    [Theory]
    [InlineData(UpdateVerdict.UpToDate, "0.1.2", "Up to date", "0.1.2 is the latest")]
    [InlineData(UpdateVerdict.Downgrade, "0.1.1", "Up to date", "newer than the latest release (0.1.1)")]
    [InlineData(UpdateVerdict.Expired, "0.1.1", "No update installed", "expired signature")]
    [InlineData(UpdateVerdict.Untrusted, null, "No update installed", "not signed with DeadlockScout's key")]
    [InlineData(UpdateVerdict.Invalid, null, "No update installed", "not signed with DeadlockScout's key")]
    public void Each_verdict_has_its_own_answer(UpdateVerdict verdict, string? latest, string title, string fragment)
    {
        var answer = ManualCheck.Describe(verdict, "0.1.2", latest);
        Assert.Equal(title, answer.Title);
        Assert.Contains(fragment, answer.Text);
    }

    [Fact]
    public void An_unreachable_github_says_try_later()
    {
        var (title, text) = ManualCheck.Describe(null, "0.1.2", null);
        Assert.Equal("Couldn't check for updates", title);
        Assert.Contains("Try again later", text);
    }

    [Fact]
    public void Not_installed_and_during_a_match_say_why_nothing_ran()
    {
        Assert.Contains("installed copy", ManualCheck.NotInstalled.Text);
        Assert.Contains("between matches", ManualCheck.DuringMatch.Text);
    }
}
