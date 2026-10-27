using DeadlockScout.Steam;
using DeadlockScout.Steam.Vdf;

namespace DeadlockScout.Tests.Steam;

public class VdfParserTests
{
    [Fact]
    public void Parses_loginusers_and_picks_the_most_recent_account()
    {
        var root = VdfParser.Parse(Fixtures.Read("steam", "loginusers.vdf"));
        Assert.Equal("Scout", root.GetString("users", "76561197960388272", "PersonaName"));
        Assert.Equal(122544u, SteamLocator.AccountFromLoginUsers(root));
    }

    [Fact]
    public void Parses_nested_objects_comments_and_escapes()
    {
        var root = VdfParser.Parse(Fixtures.Read("steam", "localconfig.vdf"));
        Assert.Equal("-novid", LaunchOptions.ReadLaunchOptions(root));
        Assert.Equal("Scout", root.GetString("UserLocalConfigStore", "friends", "PersonaName"));
        var esc = VdfParser.Parse("\"k\" \"a \\\"quoted\\\" \\\\ value\"");
        Assert.Equal("a \"quoted\" \\ value", esc.GetString("k"));
    }

    [Fact]
    public void Round_trips_through_serialize()
    {
        var root = VdfParser.Parse(Fixtures.Read("steam", "localconfig.vdf"));
        var again = VdfParser.Parse(root.Serialize());
        Assert.Equal(root.Serialize(), again.Serialize());
        Assert.Equal("-condebug -high", again.GetString("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "730", "LaunchOptions"));
    }

    [Fact]
    public void Rejects_malformed_input()
    {
        Assert.Throws<FormatException>(() => VdfParser.Parse("\"a\" {"));
        Assert.Throws<FormatException>(() => VdfParser.Parse("}"));
        Assert.Throws<FormatException>(() => VdfParser.Parse("\"a\" \"unterminated"));
    }

    [Fact]
    public void Steam_ids_convert_both_ways()
    {
        Assert.Equal(122544u, SteamIds.ToAccountId(76561197960388272));
        Assert.Equal(76561197960388272UL, SteamIds.ToSteamId64(122544));
        Assert.True(SteamIds.TryParseAny("[U:1:123456]", out var a) && a == 123456);
        Assert.True(SteamIds.TryParseAny("76561197960388272", out var b) && b == 122544);
        Assert.True(SteamIds.TryParseAny("42", out var c) && c == 42);
        Assert.False(SteamIds.TryParseAny("nope", out _));
    }
}
