using DeadlockScout.Core.History;

namespace DeadlockScout.Tests.Core;

public class PastedMatchIdTests
{
    [Theory]
    [InlineData("41223344", 41223344L)]
    [InlineData("  41223344\r\n", 41223344L)]
    [InlineData("Match ID: 41223344", 41223344L)]
    [InlineData("https://deadlock.example/matches/41223344?tab=items", 41223344L)]
    public void A_match_id_is_read_from_a_number_or_a_link(string text, long expected) => Assert.Equal(expected, PastedMatchId.Read(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]                       // too short to be a match
    [InlineData("76561197960287930")]           // a SteamID64, not a match
    [InlineData("41223344 and 41223355")]       // two different matches: which one?
    public void Anything_else_is_not_a_match_id(string? text) => Assert.Null(PastedMatchId.Read(text));
}
