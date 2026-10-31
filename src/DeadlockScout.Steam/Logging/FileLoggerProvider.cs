using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.Steam.Logging;

/// <summary>
/// Writes the app's log to one file a day under <c>%LOCALAPPDATA%\DeadlockScout\logs</c> (<c>scout-2026-10-31.log</c>), so
/// a player who started the app from its icon still has a log to look at or send. Keeps <see cref="KeepFiles"/> days;
/// a day's file stops growing at <see cref="MaxBytes"/>. The log can hold account ids and player names, which is why
/// Copy diagnostics, not this file, is what the troubleshooting page asks players to share.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int KeepFiles = 7;
    public const long MaxBytes = 10L * 1024 * 1024;
    public const string Prefix = "scout-";

    private readonly string _folder;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogLevel _minimum;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _path;
    private bool _full;

    public FileLoggerProvider(string folder, LogLevel minimum = LogLevel.Information, Func<DateTimeOffset>? now = null)
    {
        _folder = folder;
        _minimum = minimum;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The file being written to, or the one for today before the first line.</summary>
    public string CurrentPath => _path ?? Path.Combine(_folder, FileName(_now()));

    public static string FileName(DateTimeOffset day) => $"{Prefix}{day:yyyy-MM-dd}.log";

    /// <summary>"2026-10-31 07:50:12.345 INFO  MatchSession: Match 123 started", plus the exception's lines indented.</summary>
    public static string Format(DateTimeOffset at, LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append(level switch
          {
              LogLevel.Trace => "TRACE", LogLevel.Debug => "DEBUG", LogLevel.Information => "INFO ", LogLevel.Warning => "WARN ",
              LogLevel.Error => "ERROR", LogLevel.Critical => "CRIT ", _ => "     ",
          }).Append(' ')
          .Append(category[(category.LastIndexOf('.') + 1)..]).Append(": ").Append(message);
        if (ex is not null)
            foreach (var line in ex.ToString().Split('\n')) sb.Append("\n    ").Append(line.TrimEnd('\r'));
        return sb.ToString();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var at = _now();
        var line = Format(at, level, category, message, ex);
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_folder, FileName(at));
                if (path != _path) Open(path);
                if (_full || _writer is null) return;
                if (_writer.BaseStream.Length >= MaxBytes)
                {
                    _writer.WriteLine($"{Format(at, LogLevel.Warning, nameof(FileLoggerProvider), $"Log reached {MaxBytes / (1024 * 1024)} MB; nothing more today", null)}");
                    _full = true;
                    return;
                }
                _writer.WriteLine(line);
            }
            catch (IOException) { } // a log that cannot be written never stops the app
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Open(string path)
    {
        _writer?.Dispose();
        _writer = null;
        _path = path;
        _full = false;
        Directory.CreateDirectory(_folder);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        Prune();
    }

    /// <summary>Deletes all but the newest <see cref="KeepFiles"/> log files.</summary>
    private void Prune()
    {
        foreach (var old in Directory.GetFiles(_folder, $"{Prefix}*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepFiles))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
