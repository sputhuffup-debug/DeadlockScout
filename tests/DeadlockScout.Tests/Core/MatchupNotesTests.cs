using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class MatchupNotesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  buy Return Fire  ", "buy Return Fire")]
    [InlineData("respect\r\n  her ult\tat 6", "respect her ult at 6")]
    public void Notes_are_one_trimmed_line(string? text, string? expected) => Assert.Equal(expected, MatchupNotes.Clean(text));

    [Fact]
    public void Long_notes_are_cut()
    {
        var note = MatchupNotes.Clean(new string('a', 400));
        Assert.Equal(MatchupNotes.MaxLength, note!.Length);
    }

    [Fact]
    public void Setting_replacing_and_clearing_a_note()
    {
        var notes = MatchupNotes.With(null, 13, " poke her early ");
        Assert.Equal("poke her early", MatchupNotes.For(notes, 13));
        notes = MatchupNotes.With(notes, 7, "stay out of the bomb");
        notes = MatchupNotes.With(notes, 13, "rush Spirit Shielding");
        Assert.Equal("rush Spirit Shielding", MatchupNotes.For(notes, 13));
        Assert.Equal("stay out of the bomb", MatchupNotes.For(notes, 7));
        notes = MatchupNotes.With(notes, 13, "  ");
        Assert.Null(MatchupNotes.For(notes, 13));
        Assert.Single(notes);
        Assert.Null(MatchupNotes.For(notes, 0));
        Assert.Null(MatchupNotes.For(null, 7));
    }

    [Fact]
    public void Settings_only_change_when_a_note_does()
    {
        var s = new ScoutSettings();
        var a = s.WithMatchupNote(13, "poke early");
        Assert.NotSame(s, a);
        Assert.Same(a, a.WithMatchupNote(13, "  poke   early "));
        Assert.Same(s, s.WithMatchupNote(13, ""));
        Assert.Empty(a.WithMatchupNote(13, null).MatchupNotes);
    }

    [Fact]
    public void Notes_survive_a_backup()
    {
        var s = new ScoutSettings().WithMatchupNote(13, "poke early");
        var text = SettingsBackup.Write(s, "0.6.0", DateTimeOffset.UnixEpoch);
        var back = SettingsBackup.Read(text, new ScoutSettings()).Settings!;
        Assert.Equal("poke early", MatchupNotes.For(back.MatchupNotes, 13));
    }
}
