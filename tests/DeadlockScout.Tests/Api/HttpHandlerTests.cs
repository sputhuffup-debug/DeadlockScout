using System.Net;
using System.Net.Http.Headers;
using DeadlockScout.Api.Http;
using DeadlockScout.Api.Work;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Api;

public class HttpHandlerTests
{
    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public int Calls;
        public Scripted(params Func<HttpResponseMessage>[] responses) { foreach (var r in responses) _responses.Enqueue(r); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()() : new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code, int? retryAfterSeconds = null)
    {
        var r = new HttpResponseMessage(code);
        if (retryAfterSeconds is { } s) r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return r;
    }

    [Theory]
    [InlineData("https://api.deadlock-api.com/v1/players/123/match-history", EndpointFamily.MatchHistory)]
    [InlineData("https://api.deadlock-api.com/v1/players/hero-stats?account_ids=1", EndpointFamily.HeroStats)]
    [InlineData("https://api.deadlock-api.com/v1/matches/5/live/url", EndpointFamily.LiveUrl)]
    [InlineData("https://api.deadlock-api.com/v1/matches/5/metadata", EndpointFamily.Metadata)]
    [InlineData("https://api.deadlock-api.com/v1/analytics/hero-stats", EndpointFamily.Analytics)]
    [InlineData("https://assets.deadlock-api.com/v2/heroes", EndpointFamily.Assets)]
    [InlineData("https://assets-bucket.deadlock-api.com/assets-api-res/images/upgrades/x.png", EndpointFamily.Assets)]
    [InlineData("https://api.deadlock-api.com/v1/players/mmr", EndpointFamily.Default)]
    public void Endpoints_are_classified(string url, EndpointFamily expected) => Assert.Equal(expected, EndpointFamilies.Classify(new Uri(url)));

    [Fact]
    public async Task Retry_handler_honours_retry_after_then_succeeds()
    {
        var waits = new List<TimeSpan>();
        var inner = new Scripted(() => Status(HttpStatusCode.TooManyRequests, 7), () => Status(HttpStatusCode.OK));
        var handler = new RetryHandler { InnerHandler = inner, Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        using var client = new HttpClient(handler);
        var r = await client.GetAsync("https://api.deadlock-api.com/v1/x");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, waits);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Retry_handler_backs_off_exponentially_with_a_cap_and_gives_up()
    {
        var waits = new List<TimeSpan>();
        var inner = new Scripted(Enumerable.Repeat<Func<HttpResponseMessage>>(() => Status(HttpStatusCode.TooManyRequests), 10).ToArray());
        var handler = new RetryHandler { InnerHandler = inner, MaxRateLimitRetries = 3, MaxBackoff = TimeSpan.FromSeconds(5), Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        using var client = new HttpClient(handler);
        var r = await client.GetAsync("https://api.deadlock-api.com/v1/x");
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal(3, waits.Count);
        Assert.Equal(4, inner.Calls);
        Assert.True(waits[0] >= TimeSpan.FromSeconds(2) && waits[0] <= TimeSpan.FromSeconds(3));
        Assert.All(waits, w => Assert.True(w <= TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_long_retry_after_is_returned_at_once_instead_of_waited_out()
    {
        var waits = new List<TimeSpan>();
        var inner = new Scripted(() => Status(HttpStatusCode.TooManyRequests, 3575), () => Status(HttpStatusCode.OK)); // the metadata fallback's ~1 h
        var handler = new RetryHandler { InnerHandler = inner, Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        using var client = new HttpClient(handler);
        var r = await client.GetAsync("https://api.deadlock-api.com/v1/matches/1/metadata");
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Empty(waits);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Server_errors_and_transport_failures_retry_three_times()
    {
        var inner = new Scripted(() => Status(HttpStatusCode.BadGateway), () => throw new HttpRequestException("reset"), () => Status(HttpStatusCode.ServiceUnavailable), () => Status(HttpStatusCode.OK));
        var handler = new RetryHandler { InnerHandler = inner, Delay = (_, _) => Task.CompletedTask };
        using var client = new HttpClient(handler);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://api.deadlock-api.com/v1/x")).StatusCode);
        Assert.Equal(4, inner.Calls);

        var inner2 = new Scripted(Enumerable.Repeat<Func<HttpResponseMessage>>(() => Status(HttpStatusCode.InternalServerError), 10).ToArray());
        using var client2 = new HttpClient(new RetryHandler { InnerHandler = inner2, Delay = (_, _) => Task.CompletedTask });
        Assert.Equal(HttpStatusCode.InternalServerError, (await client2.GetAsync("https://api.deadlock-api.com/v1/x")).StatusCode);
        Assert.Equal(4, inner2.Calls);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        var inner = new Scripted(() => Status(HttpStatusCode.NotFound));
        using var client = new HttpClient(new RetryHandler { InnerHandler = inner, Delay = (_, _) => Task.CompletedTask });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("https://api.deadlock-api.com/v1/x")).StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Rate_limiter_spreads_a_burst_over_the_quota()
    {
        var inner = new Scripted();
        var limits = new RateLimitSettings { HeroStatsPerMin = 240 }; // 4/s, burst 60
        using var client = new HttpClient(new RateLimitedHandler(limits) { InnerHandler = inner });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 64).Select(_ => client.GetAsync("https://api.deadlock-api.com/v1/players/hero-stats?account_ids=1")).ToArray();
        await Task.WhenAll(tasks);
        // 60 pass at once; 4 more wait ~250 ms each
        Assert.Equal(64, inner.Calls);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(600), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Background_requests_wait_for_the_match_to_end_and_foreground_ones_do_not()
    {
        var gate = new MatchActivityGate();
        var inner = new Scripted();
        using var client = new HttpClient(new RateLimitedHandler(new RateLimitSettings(), gate.WaitAsync) { InnerHandler = inner });
        gate.MatchStarted();

        var fg = new HttpRequestMessage(HttpMethod.Get, "https://api.deadlock-api.com/v1/players/mmr");
        await client.SendAsync(fg).WaitAsync(TimeSpan.FromSeconds(2));

        var bg = new HttpRequestMessage(HttpMethod.Get, "https://api.deadlock-api.com/v1/matches/1/metadata");
        bg.Options.Set(RateLimitedHandler.PriorityOption, RequestPriority.Background);
        var bgTask = client.SendAsync(bg);
        await Task.Delay(100);
        Assert.False(bgTask.IsCompleted);
        gate.MatchEnded();
        await bgTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Api_key_goes_in_the_header_only()
    {
        HttpRequestMessage? seen = null;
        var inner = new Capturing(r => seen = r);
        using var client = new HttpClient(new ApiKeyHandler(() => "secret") { InnerHandler = inner });
        await client.GetAsync("https://api.deadlock-api.com/v1/x");
        Assert.Equal("secret", seen!.Headers.GetValues("X-API-KEY").Single());
        Assert.DoesNotContain("secret", seen.RequestUri!.ToString());
        Assert.NotEmpty(seen.Headers.UserAgent);
    }

    [Fact]
    public async Task Api_key_is_not_sent_to_the_image_bucket()
    {
        HttpRequestMessage? seen = null;
        var inner = new Capturing(r => seen = r);
        using var client = new HttpClient(new ApiKeyHandler(() => "secret") { InnerHandler = inner });
        await client.GetAsync("https://assets-bucket.deadlock-api.com/assets-api-res/images/upgrades/x.png");
        Assert.False(seen!.Headers.Contains("X-API-KEY"));
    }

    private sealed class Capturing(Action<HttpRequestMessage> onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { onSend(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
