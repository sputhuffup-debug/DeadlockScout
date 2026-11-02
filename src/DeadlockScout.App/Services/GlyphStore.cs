using System.IO;
using DeadlockScout.Ocr;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.App.Services;

/// <summary>
/// The glyph reader's templates on disk: %LOCALAPPDATA%\DeadlockScout\glyphs.json, next to settings.json. A few KB of
/// ink grids of the HUD's digits, nothing else; learned in the calibration tool and saved when it closes.
/// </summary>
public sealed class GlyphStore
{
    private readonly string _path;
    private readonly ILogger<GlyphStore>? _log;

    public GlyphStore(string? path = null, ILogger<GlyphStore>? log = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "glyphs.json");
        _log = log;
        Glyphs = Load();
    }

    public GlyphSet Glyphs { get; }

    private GlyphSet Load()
    {
        try { return File.Exists(_path) ? GlyphSet.FromJson(File.ReadAllText(_path)) : new GlyphSet(); }
        catch (IOException ex) { _log?.LogWarning(ex, "Glyph templates could not be read"); return new GlyphSet(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Glyphs.ToJson());
            _log?.LogInformation("Glyph reader: saved templates for {Known}", Glyphs.Known);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.LogWarning(ex, "Glyph templates could not be saved"); }
    }
}
