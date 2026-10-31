using System.Diagnostics;
using DeadlockScout.Api.Work;
using DeadlockScout.Core.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace DeadlockScout.App.Services;

/// <summary>
/// Reads our own process's CPU time and memory every few seconds while a match is live and hands them to the
/// diagnostics log, which sums them up against the 1.0 budget (<see cref="PerformanceSummary"/>). Only our process is
/// read, never the game's. Between matches it only checks a flag.
/// </summary>
public sealed class PerformanceSampler : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    private readonly DiagnosticsLog _diagnostics;
    private readonly MatchActivityGate _gate;

    public PerformanceSampler(DiagnosticsLog diagnostics, MatchActivityGate gate)
    {
        _diagnostics = diagnostics;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var self = Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_gate.IsMatchLive) continue;
                self.Refresh();
                _diagnostics.Performance(new ProcessSample(DateTimeOffset.UtcNow, self.TotalProcessorTime, self.WorkingSet64, self.PrivateMemorySize64));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
