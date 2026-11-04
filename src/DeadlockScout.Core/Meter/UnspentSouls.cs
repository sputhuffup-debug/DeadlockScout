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
    /// <summary>The default haunted souls threshold (<c>coaching.hauntedSouls</c>).</summary>
    public const int DefaultHauntedThreshold = 1_000;

    /// <param name="latest">The meter's newest sample.</param>
    /// <param name="threshold">Show from this many unspent souls; 0 or less turns that part off.</param>
    /// <param name="hauntedThreshold">
    /// Haunted souls (unsecured: lost at a death) worth a warning of their own, from this many; 0 or less turns it off.
    /// They are read off the HUD only, so this part needs OCR.
    /// </param>
    /// <returns>The line, or null when both parts are off, below their thresholds or unknown.</returns>
    public static string? Line(SoulSample? latest, int threshold, int hauntedThreshold = 0)
    {
        if (latest is null) return null;
        int haunted = latest.Haunted ?? 0;
        bool atRisk = hauntedThreshold > 0 && haunted >= hauntedThreshold;
        bool unspentLine = threshold > 0 && latest.Unspent is { } u && u >= threshold;
        if (unspentLine)
        {
            string amount = TeamSoulsLead.Short(latest.Unspent!.Value);
            string line = latest.Source == SampleSource.Ocr
                ? $"{amount} unspent: spend before you fight"
                : $"{amount} unspent at {PaceMath.FormatClock(latest.GameTime)}: spend before you fight";
            return atRisk ? $"{line} · {TeamSoulsLead.Short(haunted)} of it haunted, lost if you die" : line;
        }
        return atRisk ? $"{TeamSoulsLead.Short(haunted)} haunted souls: lost if you die, spend or secure them" : null;
    }
}
