using System.IO;
using DeadlockScout.Ocr;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.App.Services;

/// <summary>
/// The glyph reader's templates on disk: %LOCALAPPDATA%\DeadlockScout\glyphs.json, next to settings.json. A few KB of
/// ink grids of the HUD's digits, one set for the clock and one for the souls count (different fonts), nothing else;
/// learned in the calibration tool (saved when it closes) and during matches (saved at most every 30 s).
/// </summary>
public sealed class GlyphStore
{
    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly ILogger<GlyphStore>? _log;
    private readonly object _gate = new();
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private bool _pending;

    public GlyphStore(string? path = null, ILogger<GlyphStore>? log = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "glyphs.json");
        _log = log;
        Glyphs = Load();
    }

    public GlyphBook Glyphs { get; }

    private GlyphBook Load()
    {
        try
        {
            if (!File.Exists(_path)) return new GlyphBook();
            var book = GlyphBook.FromJson(File.ReadAllText(_path), out bool mixed);
            if (mixed) _log?.LogInformation("Glyph reader: the saved templates mixed the clock's and the souls' fonts; each box learns again on its own");
            return book;
        }
        catch (IOException ex) { _log?.LogWarning(ex, "Glyph templates could not be read"); return new GlyphBook(); }
    }

    /// <summary>
    /// Saves now, or <see cref="SaveEvery"/> after the last save when that was recent (learning during a match calls this
    /// often): what is learned meanwhile goes out in that one later save.
    /// </summary>
    public void SaveSoon()
    {
        lock (_gate)
        {
            var wait = _lastSave + SaveEvery - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                if (_pending) return;
                _pending = true;
                _ = Task.Delay(wait).ContinueWith(_ => Flush(), TaskScheduler.Default);
                return;
            }
        }
        Save();
    }

    /// <summary>Writes a save that <see cref="SaveSoon"/> put off (its timer, or the app closing).</summary>
    public void Flush()
    {
        bool pending;
        lock (_gate) pending = _pending;
        if (pending) Save();
    }

    public void Save()
    {
        lock (_gate) { _lastSave = DateTimeOffset.UtcNow; _pending = false; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Glyphs.ToJson());
            _log?.LogInformation("Glyph reader: saved templates (clock {Clock}, souls {Souls})", Glyphs.Clock.Known, Glyphs.Souls.Known);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.LogWarning(ex, "Glyph templates could not be saved"); }
    }
}
