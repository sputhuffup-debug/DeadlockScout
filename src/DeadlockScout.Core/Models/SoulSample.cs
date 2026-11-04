namespace DeadlockScout.Core.Models;

/// <summary>Where a soul sample came from. The meter does not care; the overlay labels it.</summary>
public enum SampleSource
{
    LiveFeed,
    Ocr,
}

/// <summary>
/// Your own souls at a game time. <paramref name="Souls"/> is net worth (what the meter and the pace curves count): the
/// value of the items you hold plus the souls you have not spent. <paramref name="Unspent"/> is the souls not spent yet,
/// when known: the HUD's counter (read by OCR), or the broadcast's net worth less the cost of the items it says you hold.
/// <para>
/// One exception, inside the soul sources only: a reading straight from <c>OcrSoulSource</c> carries the HUD counter in
/// <paramref name="Souls"/> too (it knows nothing about items); <c>OcrJumpGuard</c> turns it into net worth before the
/// meter sees it.
/// </para>
/// </summary>
/// <param name="Haunted">Of the unspent souls, the haunted ones the HUD showed (unsecured: lost at a death), from OCR; 0 or
/// null without them.</param>
public sealed record SoulSample(TimeSpan GameTime, int Souls, SampleSource Source, int? Unspent = null, int? Haunted = null);
