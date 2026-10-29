using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class SettingsFieldsTests
{
    [Theory]
    [InlineData("2:00", 120)]
    [InlineData(" 1:25 ", 85)]
    [InlineData("90", 90)]
    [InlineData("0:05", 5)]
    [InlineData("10:00", 600)]
    public void Clocks_read_as_minutes_and_seconds_or_plain_seconds(string text, int seconds)
        => Assert.Equal(seconds, SettingsFields.ReadClock(text));

    [Theory]
    [InlineData("")]
    [InlineData("2:5")]      // seconds take two digits
    [InlineData("2:60")]
    [InlineData("-30")]
    [InlineData("1:2:3")]
    [InlineData("soon")]
    public void Anything_else_is_rejected(string text) => Assert.Null(SettingsFields.ReadClock(text));

    [Fact]
    public void Clocks_write_back_as_typed_and_numbers_stay_in_range()
    {
        Assert.Equal("2:00", SettingsFields.Clock(120));
        Assert.Equal("0:05", SettingsFields.Clock(5));
        Assert.Null(SettingsFields.ReadClock("4:00", maxSeconds: 180));
        Assert.Equal(30, SettingsFields.ReadInt(" 30 ", 0, 120));
        Assert.Null(SettingsFields.ReadInt("121", 0, 120));
        Assert.Null(SettingsFields.ReadInt("3.5", 0, 120));
    }
}
