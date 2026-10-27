using DeadlockScout.Steam;
using DeadlockScout.Steam.Vdf;

namespace DeadlockScout.Tests.Steam;

public class LaunchOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("-novid", false)]
    [InlineData("-condebug", true)]
    [InlineData("-novid -CONDEBUG -high", true)]
    [InlineData("-condebugx", false)]
    public void Detects_condebug(string? options, bool expected) => Assert.Equal(expected, LaunchOptions.HasCondebug(options));

    [Fact]
    public void Appends_without_disturbing_other_flags()
    {
        Assert.Equal("-condebug", LaunchOptions.WithCondebug(null));
        Assert.Equal("-novid -condebug", LaunchOptions.WithCondebug("-novid "));
        Assert.Equal("-condebug", LaunchOptions.WithCondebug("-condebug"));
    }

    [Fact]
    public void Editing_creates_the_app_node_when_missing()
    {
        var root = VdfParser.Parse("\"UserLocalConfigStore\" { \"Software\" { } }");
        var node = root.GetOrAddObject("UserLocalConfigStore").GetOrAddObject("Software").GetOrAddObject("Valve")
            .GetOrAddObject("Steam").GetOrAddObject("apps").GetOrAddObject("1422450");
        node.Set("LaunchOptions", LaunchOptions.WithCondebug(null));
        var again = VdfParser.Parse(root.Serialize());
        Assert.Equal("-condebug", LaunchOptions.ReadLaunchOptions(again));
    }
}
