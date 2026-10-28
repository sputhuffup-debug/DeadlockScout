using System.Runtime.CompilerServices;
using System.Text;

namespace DeadlockScout.Steam.Logs;

/// <summary>
/// Polling tailer for a log the game keeps open and appends to. Handles the file not existing yet, truncation and
/// rotation. Yields complete lines only. Starts at the end of the file unless <paramref name="fromStart"/> or a start
/// offset is given, so an old match is never replayed (ConsoleLogTailer reads what is already there itself, once).
/// </summary>
public sealed class LogTailer
{
    private readonly string _path;
    private readonly TimeSpan _poll;
    private readonly bool _fromStart;
    private readonly long? _startAt;

    /// <param name="startAt">Byte offset to start from (where a caller's own read of the file stopped); overrides <paramref name="fromStart"/>.</param>
    public LogTailer(string path, TimeSpan? poll = null, bool fromStart = false, long? startAt = null)
    {
        _path = path;
        _poll = poll ?? DefaultPoll;
        _fromStart = fromStart;
        _startAt = startAt;
    }

    /// <summary>A lobby line a second late changes nothing; nothing in the app needs faster.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    public string Path => _path;

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        long position = -1;
        var buffer = new StringBuilder();
        var bytes = new byte[64 * 1024];

        while (!ct.IsCancellationRequested)
        {
            FileStream? fs = null;
            try
            {
                if (!File.Exists(_path))
                {
                    position = -1;
                    await Task.Delay(_poll, ct).ConfigureAwait(false);
                    continue;
                }
                fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                long length = fs.Length;
                if (position < 0) position = _startAt is { } at && at <= length ? at : _fromStart ? 0 : length;
                if (length < position)
                {
                    // Truncated or rotated: start over from the top.
                    position = 0;
                    buffer.Clear();
                }
                if (length == position)
                {
                    fs.Dispose(); fs = null;
                    await Task.Delay(_poll, ct).ConfigureAwait(false);
                    continue;
                }
                fs.Seek(position, SeekOrigin.Begin);
                int read;
                while ((read = await fs.ReadAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false)) > 0)
                {
                    position += read;
                    buffer.Append(Encoding.UTF8.GetString(bytes, 0, read));
                    int nl;
                    while ((nl = IndexOfNewline(buffer)) >= 0)
                    {
                        string line = buffer.ToString(0, nl).TrimEnd('\r');
                        buffer.Remove(0, nl + 1);
                        yield return line;
                    }
                }
            }
            finally
            {
                fs?.Dispose();
            }
        }
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') return i;
        return -1;
    }
}
