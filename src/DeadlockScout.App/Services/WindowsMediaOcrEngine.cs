using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace DeadlockScout.App.Services;

/// <summary>
/// The built-in Windows OCR engine (Windows.Media.Ocr), reading one prepared region at a time. Uses the first language
/// of your Windows profile that has OCR support, else English; unavailable when no OCR language is installed. The engine
/// itself (and its language model, tens of MB of native memory) loads on the first read, so with OCR off it never does;
/// which language it will use is known at once from the list of installed OCR languages.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine, IDisposable
{
    private readonly string? _language;
    private readonly Lazy<OcrEngine?> _engine;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1); // one recognition at a time per engine

    public WindowsMediaOcrEngine(ILogger<WindowsMediaOcrEngine>? log = null)
    {
        _log = log ?? NullLogger<WindowsMediaOcrEngine>.Instance;
        try { _language = PickLanguage(); }
        catch (Exception ex) { _log.LogWarning(ex, "Windows OCR's languages could not be read"); }
        if (_language is null) _log.LogInformation("Windows OCR is unavailable: no OCR language is installed");
        _engine = new Lazy<OcrEngine?>(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Name => _language is null ? "Windows OCR (no OCR language installed)" : $"Windows OCR ({_language})";
    public bool IsAvailable => _language is not null;
    /// <summary>Whether the engine has been loaded (by a first read); for the memory line in Diagnostics.</summary>
    public bool Loaded => _engine.IsValueCreated && _engine.Value is not null;

    /// <summary>The first profile language Windows OCR supports, else en-US when installed, else none.</summary>
    private static string? PickLanguage()
    {
        foreach (var tag in Windows.System.UserProfile.GlobalizationPreferences.Languages)
            if (OcrEngine.IsLanguageSupported(new Language(tag))) return tag;
        return OcrEngine.IsLanguageSupported(new Language("en-US")) ? "en-US" : null;
    }

    private OcrEngine? Create()
    {
        if (_language is null) return null;
        try
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(_language)) ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null) _log.LogInformation("Windows OCR loaded ({Language})", engine.RecognizerLanguage.LanguageTag);
            return engine;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Windows OCR could not start"); return null; }
    }

    public async Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct)
    {
        if (_engine.Value is not { } engine) return null;
        var prepared = OcrPreprocess.Prepare(region);
        if (prepared.Width > OcrEngine.MaxImageDimension || prepared.Height > OcrEngine.MaxImageDimension) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(prepared.Bgra.ToArray()),
                BitmapPixelFormat.Bgra8, prepared.Width, prepared.Height, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            return result.Text;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
