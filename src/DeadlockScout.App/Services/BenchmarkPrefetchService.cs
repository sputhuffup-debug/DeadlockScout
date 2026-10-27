using DeadlockScout.Api.Http;
using DeadlockScout.Api.Work;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// Runs <see cref="BenchmarkWarmer"/> between matches: a pass waits until no match is live, then repeats every
/// <see cref="MeterSettings.PrefetchIntervalMinutes"/>. Background requests inside a pass also wait if a match starts.
/// </summary>
public sealed class BenchmarkPrefetchService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly BenchmarkWarmer _warmer;
    private readonly MatchActivityGate _gate;
    private readonly GamePaths _paths;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger<BenchmarkPrefetchService> _log;

    public BenchmarkPrefetchService(BenchmarkWarmer warmer, MatchActivityGate gate, GamePaths paths, IOptionsMonitor<ScoutSettings> settings,
                                    ILogger<BenchmarkPrefetchService> log)
    {
        _warmer = warmer;
        _gate = gate;
        _paths = paths;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false); // let the lobby path have the network first
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _gate.WaitAsync(RequestPriority.Background, ct).ConfigureAwait(false);
                    await _warmer.WarmAsync(_paths.AccountId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Benchmark prefetch pass failed"); }
                var minutes = Math.Max(5, _settings.CurrentValue.Meter.PrefetchIntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
