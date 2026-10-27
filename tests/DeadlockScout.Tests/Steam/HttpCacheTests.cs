using System.Text;
using DeadlockScout.Core.Models;
using DeadlockScout.Steam;

namespace DeadlockScout.Tests.Steam;

public class HttpCacheTests
{
    [Fact]
    public void Extracts_both_salts_for_a_match_from_binary_noise()
    {
        var bytes = new List<byte> { 0, 1, 2, 255 };
        bytes.AddRange(Encoding.ASCII.GetBytes("GET http://replay182.valve.net/1422450/41223344_12345.meta.bz2?v=2 HTTP/1.1"));
        bytes.AddRange(new byte[] { 0, 0, 7 });
        bytes.AddRange(Encoding.ASCII.GetBytes("http://replay182.valve.net/1422450/41223344_67890.dem.bz2\n"));
        bytes.AddRange(Encoding.ASCII.GetBytes("http://replay5.valve.net/570/1_2.dem.bz2")); // another game
        bytes.AddRange(Encoding.ASCII.GetBytes("http://replay9.valve.net/1422450/50000000_1.dem.bz2"));
        var salts = SteamHttpCache.ExtractSalts(bytes.ToArray());
        Assert.Equal(2, salts.Count);
        var a = salts.Single(s => s.MatchId == 41223344);
        Assert.Equal(new MatchSalts(41223344, 182, 12345, 67890), a);
        var b = salts.Single(s => s.MatchId == 50000000);
        Assert.Null(b.MetadataSalt);
        Assert.Equal(1, b.ReplaySalt);
    }

    [Fact]
    public void Scans_recent_files_only()
    {
        var dir = Directory.CreateTempSubdirectory("dls-steam");
        var cacheDir = Path.Combine(dir.FullName, "appcache", "httpcache", "0a");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "old"), "http://replay1.valve.net/1422450/100_1.meta.bz2");
        File.SetLastWriteTimeUtc(Path.Combine(cacheDir, "old"), DateTime.UtcNow.AddDays(-3));
        File.WriteAllText(Path.Combine(cacheDir, "new"), "junk http://replay2.valve.net/1422450/200_2.dem.bz2 junk");
        var cache = new SteamHttpCache(dir.FullName);
        Assert.True(cache.Exists);
        var salts = cache.ScanSalts(DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(200, salts.Single().MatchId);
        Assert.Null(cache.TryReadMatch(200));
        dir.Delete(true);
    }
}
