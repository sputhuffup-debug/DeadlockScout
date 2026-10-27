using System.Runtime.CompilerServices;
using System.Text;

namespace DeadlockScout.Api.Http;

/// <summary>One server-sent event. <see cref="Data"/> joins multi-line data with "\n" per the spec.</summary>
public sealed record SseEvent(string? Event, string Data, string? Id, TimeSpan? Retry);

/// <summary>Minimal text/event-stream parser: comments, multi-line data, event/id/retry fields, CRLF tolerant.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        string? ev = null, id = null;
        TimeSpan? retry = null;
        var data = new StringBuilder();
        bool hasData = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseEvent(ev, data.ToString(), id, retry);
                }
                ev = null; data.Clear(); hasData = false; retry = null;
                continue;
            }
            if (line[0] == ':') continue;
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "event": ev = value; break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value); hasData = true; break;
                case "id": id = value; break;
                case "retry": if (int.TryParse(value, out var ms)) retry = TimeSpan.FromMilliseconds(ms); break;
            }
        }
        if (hasData) yield return new SseEvent(ev, data.ToString(), id, retry);
    }
}
