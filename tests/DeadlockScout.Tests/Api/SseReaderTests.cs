using System.Text;
using DeadlockScout.Api.Http;

namespace DeadlockScout.Tests.Api;

public class SseReaderTests
{
    private static async Task<List<SseEvent>> Parse(string text)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var list = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(ms, default)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Parses_events_with_fields_comments_and_multiline_data()
    {
        var events = await Parse(": keep-alive\r\nevent: update\nid: 7\nretry: 3000\ndata: {\"a\":1,\ndata: \"b\":2}\n\ndata:plain\n\n\n");
        Assert.Equal(2, events.Count);
        Assert.Equal("update", events[0].Event);
        Assert.Equal("7", events[0].Id);
        Assert.Equal(TimeSpan.FromSeconds(3), events[0].Retry);
        Assert.Equal("{\"a\":1,\n\"b\":2}", events[0].Data);
        Assert.Null(events[1].Event);
        Assert.Equal("plain", events[1].Data);
    }

    [Fact]
    public async Task Final_event_without_trailing_blank_line_is_still_delivered()
    {
        var events = await Parse("data: last");
        Assert.Single(events);
        Assert.Equal("last", events[0].Data);
    }
}
