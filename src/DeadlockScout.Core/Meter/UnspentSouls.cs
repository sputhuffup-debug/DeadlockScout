using System.Globalization;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Meter;

/// <summary>
/// "3.4k unspent: spend before you fight": souls sitting unspent are no help in a fight, so over a threshold the pace
/// strip says so. The souls come from the newest soul sample's <see cref="SoulSample.Unspent"/>: with OCR on that is the
/// HUD's counter, live; from the broadcast it is net worth less the items held, about 35 s old, so the line names its
/// game time. Pure, so tested; a computed value, never stored.
/// </summary>
public static class UnspentSouls
{
    /// <summary>The default threshold (<c>coaching.unspentSouls</c>).</summary>
    public const int DefaultThreshold = 3_000;

    /// <param name="latest">The meter's newest sample.</param>
    /// <param name="threshold">Show from this many unspent souls; 0 or less turns the line off.</param>
    /// <returns>The line, or null when it is off, below the threshold or unknown.</returns>
    public static string? Line(SoulSample? latest, int threshold)
    {
        if (threshold <= 0 || latest?.Unspent is not { } unspent || unspent < threshold) return null;
        string amount = TeamSoulsLead.Short(unspent);
        return latest.Source == SampleSource.Ocr
            ? $"{amount} unspent: spend before you fight"
            : $"{amount} unspent at {PaceMath.FormatClock(latest.GameTime)}: spend before you fight";
    }
}
