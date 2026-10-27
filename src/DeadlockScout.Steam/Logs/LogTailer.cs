using System.Runtime.CompilerServices;
using System.Text;

namespace DeadlockScout.Steam.Logs;

/// <summary>
/// Polling tailer for a log the game keeps open and appends to. Handles the file not existing yet, truncation and
/// rotation. Yields complete lines only. Starts at the end of the file unless <paramref name="fromStart"/> is set,
/// so an old match is never replayed.
/// </summary>
public sealed class LogTailer
{
    private readonly string _path;
    private readonly TimeSpan _poll;
    private readonly bool _fromStart;

    public LogTailer(string path, TimeSpan? poll = null, bool fromStart = false)
    {
        _path = path;
        _poll = poll ?? TimeSpan.FromMilliseconds(500);
        _fromStart = fromStart;
    }

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
                if (position < 0) position = _fromStart ? 0 : length;
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
