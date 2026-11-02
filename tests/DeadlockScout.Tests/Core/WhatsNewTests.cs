using DeadlockScout.Core.Updates;

namespace DeadlockScout.Tests.Core;

public class WhatsNewTests
{
    [Fact]
    public void A_newer_version_than_the_last_run_links_its_release_notes()
    {
        Assert.Equal("https://github.com/sputhuffup-debug/DeadlockScout/releases/tag/v0.2.0", WhatsNew.NotesFor("0.1.4", new Version(0, 2, 0, 0)));
        Assert.Equal(WhatsNew.ReleasesPage + "0.2.1", WhatsNew.NotesFor("0.2.0", new Version(0, 2, 1)));
    }

    [Fact]
    public void A_first_install_the_same_version_or_an_older_one_says_nothing()
    {
        Assert.Null(WhatsNew.NotesFor(null, new Version(0, 2, 0)));
        Assert.Null(WhatsNew.NotesFor("", new Version(0, 2, 0)));
        Assert.Null(WhatsNew.NotesFor("0.2.0", new Version(0, 2, 0, 0)));   // the revision does not count
        Assert.Null(WhatsNew.NotesFor("0.3.0", new Version(0, 2, 0)));
        Assert.Null(WhatsNew.NotesFor("not a version", new Version(0, 2, 0)));
    }

    [Fact]
    public void The_kept_version_has_three_parts()
    {
        Assert.Equal("0.2.0", WhatsNew.Keep(new Version(0, 2, 0, 0)));
        Assert.Equal("1.0.0", WhatsNew.Keep(new Version(1, 0)));
    }
}
