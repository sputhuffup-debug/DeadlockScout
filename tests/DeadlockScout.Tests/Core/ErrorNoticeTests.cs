using DeadlockScout.Core.Diagnostics;

namespace DeadlockScout.Tests.Core;

public class ErrorNoticeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_tray_says_it_once_then_counts_the_errors_until_the_quiet_time_is_over()
    {
        var n = new ErrorNotice();
        Assert.StartsWith("InvalidOperationException: boom", n.Seen(new InvalidOperationException("boom"), T0));
        Assert.Null(n.Seen(new InvalidOperationException("again"), T0.AddMinutes(1)));
        Assert.Null(n.Seen(new InvalidOperationException("again"), T0.AddMinutes(5)));
        var later = n.Seen(new TimeoutException("slow"), T0.AddMinutes(11))!;
        Assert.StartsWith("3 errors since the last notice, the latest: TimeoutException: slow", later);
        Assert.Equal(4, n.Total);
    }

    [Fact]
    public void Describe_takes_the_inner_error_and_keeps_it_short()
    {
        var wrapped = new AggregateException(new ArgumentException(new string('x', 300)));
        var text = ErrorNotice.Describe(wrapped);
        Assert.StartsWith("ArgumentException: xxx", text);
        Assert.True(text.Length < 170);
        Assert.Equal("Exception", ErrorNotice.Describe(new Exception("")));
    }
}
