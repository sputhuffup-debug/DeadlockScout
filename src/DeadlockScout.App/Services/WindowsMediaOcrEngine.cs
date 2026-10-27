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
/// of your Windows profile that has OCR support, else English; unavailable when no OCR language is installed.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine, IDisposable
{
    private readonly OcrEngine? _engine;
    private readonly SemaphoreSlim _gate = new(1, 1); // one recognition at a time per engine

    public WindowsMediaOcrEngine(ILogger<WindowsMediaOcrEngine>? log = null)
    {
        log ??= NullLogger<WindowsMediaOcrEngine>.Instance;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? (OcrEngine.IsLanguageSupported(new Language("en-US")) ? OcrEngine.TryCreateFromLanguage(new Language("en-US")) : null);
        }
        catch (Exception ex) { log.LogWarning(ex, "Windows OCR could not start"); }
        if (_engine is null) log.LogInformation("Windows OCR is unavailable: no OCR language is installed");
    }

    public string Name => _engine is null ? "Windows OCR (no OCR language installed)" : $"Windows OCR ({_engine.RecognizerLanguage.LanguageTag})";
    public bool IsAvailable => _engine is not null;

    public async Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct)
    {
        if (_engine is null) return null;
        var prepared = OcrPreprocess.Prepare(region);
        if (prepared.Width > OcrEngine.MaxImageDimension || prepared.Height > OcrEngine.MaxImageDimension) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(prepared.Bgra.ToArray()),
                BitmapPixelFormat.Bgra8, prepared.Width, prepared.Height, BitmapAlphaMode.Premultiplied);
            var result = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            return result.Text;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
