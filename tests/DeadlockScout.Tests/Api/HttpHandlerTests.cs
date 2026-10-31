using System.Net;
using System.Net.Http.Headers;
using DeadlockScout.Api.Http;
using DeadlockScout.Api.Work;
using DeadlockScout.Core.Abstractions;
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
    public async Task Steam_is_busy_with_a_long_retry_after_is_returned_at_once()
    {
        // deadlock-api's "Steam is busy" (live, 2026-10-31): 503 with Retry-After 1913.
        var waits = new List<TimeSpan>();
        var inner = new Scripted(() => Status(HttpStatusCode.ServiceUnavailable, 1913), () => Status(HttpStatusCode.OK));
        var handler = new RetryHandler { InnerHandler = inner, Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        using var client = new HttpClient(handler);
        var r = await client.GetAsync("https://api.deadlock-api.com/v1/matches/1/metadata");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
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
    public async Task Background_requests_are_deferred_during_a_match_and_foreground_ones_are_not()
    {
        var gate = new MatchActivityGate();
        var inner = new Scripted();
        using var client = new HttpClient(new RateLimitedHandler(new RateLimitSettings(), gate.WaitAsync) { InnerHandler = inner });
        gate.MatchStarted();

        var fg = new HttpRequestMessage(HttpMethod.Get, "https://api.deadlock-api.com/v1/players/mmr");
        await client.SendAsync(fg).WaitAsync(TimeSpan.FromSeconds(2));

        // Held until the match ends it would only meet HttpClient's 30 s timeout: it is refused at once, never sent.
        HttpRequestMessage Background()
        {
            var bg = new HttpRequestMessage(HttpMethod.Get, "https://api.deadlock-api.com/v1/matches/1/metadata");
            bg.Options.Set(RateLimitedHandler.PriorityOption, RequestPriority.Background);
            return bg;
        }
        await Assert.ThrowsAsync<BackgroundDeferredException>(() => client.SendAsync(Background()).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, inner.Calls);

        gate.MatchEnded();
        await client.SendAsync(Background()).WaitAsync(TimeSpan.FromSeconds(2));
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

    [Theory]
    [InlineData("https://api.deadlock-api.com/v1/x", true)]
    [InlineData("https://assets.deadlock-api.com/v2/heroes", true)]
    [InlineData("https://deadlock-api.com/", true)]
    [InlineData("https://assets-bucket.deadlock-api.com/x.png", false)]
    [InlineData("http://api.deadlock-api.com/v1/x", false)]
    [InlineData("https://evil.example/v1/x", false)]
    [InlineData("https://deadlock-api.com.evil.example/v1/x", false)]
    [InlineData("https://notdeadlock-api.com/v1/x", false)]
    [InlineData("http://localhost:3000/v1/x", true)]
    [InlineData("http://localhost:4000/v1/x", false)]
    public void The_key_goes_only_to_deadlock_api_or_the_configured_address(string url, bool carries)
        => Assert.Equal(carries, ApiKeyHandler.MayCarryKey(new Uri(url), "http://localhost:3000"));

    [Fact]
    public async Task Api_key_is_not_sent_to_another_host_and_a_hand_set_one_is_removed()
    {
        HttpRequestMessage? seen = null;
        using var client = new HttpClient(new ApiKeyHandler(() => "secret") { InnerHandler = new Capturing(r => seen = r) });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/v1/x");
        request.Headers.TryAddWithoutValidation(ApiKeyHandler.Header, "secret");
        await client.SendAsync(request);
        Assert.False(seen!.Headers.Contains(ApiKeyHandler.Header));
    }

    /// <summary>Answers each URL from a script and records what each request carried.</summary>
    private sealed class Redirecting(Dictionary<string, Func<HttpResponseMessage>> script) : HttpMessageHandler
    {
        public readonly List<(string Url, string Method, string? Key)> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!.ToString(), request.Method.Method,
                request.Headers.TryGetValues(ApiKeyHandler.Header, out var v) ? v.Single() : null));
            return Task.FromResult(script.TryGetValue(request.RequestUri.ToString(), out var make) ? make() : new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static Func<HttpResponseMessage> RedirectTo(string location, HttpStatusCode code = HttpStatusCode.Found)
        => () => { var r = new HttpResponseMessage(code); r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return r; };

    private static HttpClient Pipeline(Redirecting wire)
        => new(new RedirectHandler { InnerHandler = new ApiKeyHandler(() => "secret") { InnerHandler = wire } });

    [Fact]
    public async Task A_redirect_to_another_host_is_followed_without_the_key()
    {
        var wire = new Redirecting(new()
        {
            ["https://api.deadlock-api.com/v1/a"] = RedirectTo("https://evil.example/collect"),
        });
        using var client = Pipeline(wire);
        var response = await client.GetAsync("https://api.deadlock-api.com/v1/a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, wire.Seen.Count);
        Assert.Equal("secret", wire.Seen[0].Key);
        Assert.Equal("https://evil.example/collect", wire.Seen[1].Url);
        Assert.Null(wire.Seen[1].Key);
    }

    [Fact]
    public async Task A_redirect_within_deadlock_api_keeps_the_key_and_relative_locations_resolve()
    {
        var wire = new Redirecting(new()
        {
            ["https://api.deadlock-api.com/v1/a"] = RedirectTo("/v1/b", HttpStatusCode.TemporaryRedirect),
        });
        using var client = Pipeline(wire);
        await client.GetAsync("https://api.deadlock-api.com/v1/a");
        Assert.Equal("https://api.deadlock-api.com/v1/b", wire.Seen[1].Url);
        Assert.Equal("secret", wire.Seen[1].Key);
    }

    [Fact]
    public async Task Redirects_never_downgrade_to_http_and_stop_after_the_limit()
    {
        var down = new Redirecting(new() { ["https://api.deadlock-api.com/v1/a"] = RedirectTo("http://api.deadlock-api.com/v1/a") });
        using (var client = Pipeline(down))
        {
            var response = await client.GetAsync("https://api.deadlock-api.com/v1/a");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Single(down.Seen);
        }

        var loop = new Redirecting(new() { ["https://api.deadlock-api.com/v1/a"] = RedirectTo("https://api.deadlock-api.com/v1/a") });
        using (var client = Pipeline(loop))
        {
            var response = await client.GetAsync("https://api.deadlock-api.com/v1/a");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(1 + RedirectHandler.MaxRedirects, loop.Seen.Count);
        }
    }

    [Fact]
    public async Task A_post_is_not_replayed_on_a_307_but_becomes_a_get_on_a_303()
    {
        var wire = new Redirecting(new()
        {
            ["https://api.deadlock-api.com/v1/keep"] = RedirectTo("https://api.deadlock-api.com/v1/other", HttpStatusCode.TemporaryRedirect),
            ["https://api.deadlock-api.com/v1/see"] = RedirectTo("https://api.deadlock-api.com/v1/result", HttpStatusCode.SeeOther),
        });
        using var client = Pipeline(wire);
        var kept = await client.PostAsync("https://api.deadlock-api.com/v1/keep", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, kept.StatusCode);
        await client.PostAsync("https://api.deadlock-api.com/v1/see", new StringContent("{}"));
        Assert.Equal(("https://api.deadlock-api.com/v1/result", "GET"), (wire.Seen[^1].Url, wire.Seen[^1].Method));
    }

    private sealed class Capturing(Action<HttpRequestMessage> onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { onSend(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
