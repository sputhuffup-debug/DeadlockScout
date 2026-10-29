using DeadlockScout.Core.Setup;
using DeadlockScout.Steam;
using DeadlockScout.Steam.Vdf;

namespace DeadlockScout.Tests.Steam;

public class VideoConfigTests
{
    private static DisplayMode Mode(string fullscreen, string? noBorder)
        => VideoConfig.Read(VdfParser.Parse($"\"video.cfg\"\n{{\n\t\"setting.fullscreen\"\t\t\"{fullscreen}\"\n" +
                                            (noBorder is null ? "" : $"\t\"setting.nowindowborder\"\t\t\"{noBorder}\"\n") + "}\n"));

    [Fact]
    public void The_synthetic_fixture_reads_as_borderless()
        => Assert.Equal(DisplayMode.Borderless, VideoConfig.Read(VdfParser.ParseFile(Fixtures.Path("steam", "video.txt"))));

    [Fact]
    public void Fullscreen_one_is_exclusive_and_the_border_flag_tells_windowed_from_borderless()
    {
        Assert.Equal(DisplayMode.Fullscreen, Mode("1", "1"));
        Assert.Equal(DisplayMode.Borderless, Mode("0", "1"));
        Assert.Equal(DisplayMode.Windowed, Mode("0", "0"));
        Assert.Equal(DisplayMode.Windowed, Mode("0", null));
        Assert.Equal(DisplayMode.Unknown, VideoConfig.Read(VdfParser.Parse("\"video.cfg\"\n{\n\t\"Version\"\t\"16\"\n}\n")));
    }

    [Fact]
    public void A_missing_file_or_folder_is_unknown()
    {
        Assert.Equal(DisplayMode.Unknown, VideoConfig.ReadFile(null));
        Assert.Equal(DisplayMode.Unknown, VideoConfig.ReadFile(Path.Combine(Path.GetTempPath(), "no-such-deadlock-" + Guid.NewGuid())));
        Assert.EndsWith(Path.Combine("game", "citadel", "cfg", "video.txt"), VideoConfig.PathFor("D:/Deadlock"));
    }
}
