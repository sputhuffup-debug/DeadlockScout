using System.Globalization;

namespace DeadlockScout.Core.Diagnostics;

/// <summary>
/// One reading of our own process: its total CPU time so far and its memory, at a wall-clock time. <paramref name="ManagedBytes"/>
/// is the .NET heap (our objects and the broadcast decoder's); the rest of private memory is native (WPF's rendering,
/// Windows OCR, SQLite), which tells where a memory overrun lives.
/// </summary>
public sealed record ProcessSample(DateTimeOffset At, TimeSpan CpuTime, long WorkingSetBytes, long PrivateBytes, long ManagedBytes = 0);

/// <summary>
/// The app's cost during a match, against the 1.0 budget: under 2% CPU on average (of the whole machine, all cores)
/// and under 200 MB of working set at its peak. CPU is the CPU time used between two samples over the wall time
/// between them, divided by the core count; the average is total CPU time over total wall time, so long and short
/// gaps between samples weigh what they should.
/// </summary>
public sealed record PerformanceSummary(
    int Samples,
    TimeSpan Measured,
    double AverageCpuPercent,
    double P95CpuPercent,
    double PeakCpuPercent,
    long PeakWorkingSetBytes,
    long PeakPrivateBytes,
    /// <summary>The working set at the first reading of the match: what the app weighs before the match adds to it.</summary>
    long StartWorkingSetBytes = 0,
    long PeakManagedBytes = 0)
{
    public const double CpuBudgetPercent = 2;
    public const long MemoryBudgetBytes = 200L * 1024 * 1024;
    /// <summary>Under this much measured time the numbers say too little to judge.</summary>
    public static readonly TimeSpan MinMeasured = TimeSpan.FromMinutes(5);

    public bool CpuWithin => AverageCpuPercent < CpuBudgetPercent;
    public bool MemoryWithin => PeakWorkingSetBytes < MemoryBudgetBytes;
    public bool Enough => Measured >= MinMeasured;

    /// <summary>"CPU 0.8% average (p95 1.6%, peak 4.1%) · memory 148 MB peak (private 121 MB) · within budget (2%, 200 MB)".</summary>
    public string Line
    {
        get
        {
            var c = CultureInfo.InvariantCulture;
            string verdict = !Enough ? $"too short to judge ({Format(Measured)} measured)"
                : CpuWithin && MemoryWithin ? "within budget"
                : "over budget: " + string.Join(" and ", new[] { CpuWithin ? null : "CPU", MemoryWithin ? null : "memory" }.Where(x => x is not null));
            return $"CPU {AverageCpuPercent.ToString("0.0", c)}% average (p95 {P95CpuPercent.ToString("0.0", c)}%, peak {PeakCpuPercent.ToString("0.0", c)}%)" +
                   $" · memory {Mb(PeakWorkingSetBytes)} MB peak (private {Mb(PeakPrivateBytes)} MB" +
                   (PeakManagedBytes > 0 ? $", .NET heap {Mb(PeakManagedBytes)} MB" : "") +
                   (StartWorkingSetBytes > 0 ? $"; {Mb(StartWorkingSetBytes)} MB at the start" : "") + ")" +
                   $" · {verdict} ({CpuBudgetPercent.ToString("0", c)}%, {Mb(MemoryBudgetBytes)} MB)";
        }
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture);
    private static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalSeconds} s";
}

/// <summary>Collects <see cref="ProcessSample"/>s for one match and sums them up. Not thread-safe; the caller locks.</summary>
public sealed class PerformanceMeter
{
    private readonly int _cores;
    private readonly List<double> _cpu = new();
    private ProcessSample? _first, _last;
    private long _peakWorkingSet, _peakPrivate, _peakManaged;
    private int _samples;

    public PerformanceMeter(int cores) => _cores = Math.Max(1, cores);

    public void Add(ProcessSample s)
    {
        if (_last is { } prev)
        {
            var wall = s.At - prev.At;
            if (wall <= TimeSpan.Zero || s.CpuTime < prev.CpuTime) return; // out of order or a counter reset: skip it
            _cpu.Add(Percent(s.CpuTime - prev.CpuTime, wall));
        }
        _first ??= s;
        _last = s;
        _samples++;
        _peakWorkingSet = Math.Max(_peakWorkingSet, s.WorkingSetBytes);
        _peakPrivate = Math.Max(_peakPrivate, s.PrivateBytes);
        _peakManaged = Math.Max(_peakManaged, s.ManagedBytes);
    }

    /// <summary>Null until two samples give a CPU reading.</summary>
    public PerformanceSummary? Summary()
    {
        if (_first is not { } first || _last is not { } last || _cpu.Count == 0) return null;
        var sorted = _cpu.OrderBy(x => x).ToList();
        double p95 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
        return new PerformanceSummary(_samples, last.At - first.At, Percent(last.CpuTime - first.CpuTime, last.At - first.At),
            p95, sorted[^1], _peakWorkingSet, _peakPrivate, first.WorkingSetBytes, _peakManaged);
    }

    private double Percent(TimeSpan cpu, TimeSpan wall) => wall <= TimeSpan.Zero ? 0 : cpu.TotalSeconds / wall.TotalSeconds / _cores * 100;
}
