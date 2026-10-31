using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeadlockScout.Api.Updates;
using DeadlockScout.Core.Updates;

namespace DeadlockScout.Tests.Api;

/// <summary>A fake GitHub and a key made for the test run; nothing here touches the network or the real keys.</summary>
public class UpdateClientTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Version Running = new(1, 0, 0);
    private const string Base = "https://github.com/sputhuffup-debug/DeadlockScout/releases/download/v1.0.1/";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dls-upd-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();
    private readonly byte[] _installer = Encoding.UTF8.GetBytes("pretend this is an installer");

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private sealed class FakeGitHub(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = routes.TryGetValue(request.RequestUri!.ToString(), out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
            r.RequestMessage ??= request;
            return Task.FromResult(r);
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private UpdateClient Client(params string[] keys)
        => new(new HttpClient(new FakeGitHub(_routes)), keys.Length > 0 ? keys : new[] { _key.ExportSubjectPublicKeyInfoPem() });

    /// <summary>A published v1.0.1 with a manifest signed by <paramref name="signer"/> (the test key by default).</summary>
    private void Publish(ECDsa? signer = null, string installerName = "DeadlockScout-Setup-1.0.1.exe", bool withManifest = true)
    {
        var assets = new List<string> { $$"""{"name":"{{installerName}}","browser_download_url":"{{Base}}{{installerName}}","size":{{_installer.Length}}}""" };
        if (withManifest)
        {
            var manifest = new ReleaseManifest(ReleaseManifest.ProductName, "1.0.1", Now.AddDays(-1), Now.AddDays(89), "DeadlockScout-Setup-1.0.1.exe",
                UpdateTrust.Sha256(new MemoryStream(_installer)), _installer.Length).ToBytes();
            var sig = (signer ?? _key).SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            _routes[Base + ReleaseManifest.FileName] = () => Ok(manifest);
            _routes[Base + ReleaseManifest.SignatureFileName] = () => Ok(Encoding.ASCII.GetBytes(Convert.ToBase64String(sig)));
            assets.Add($$"""{"name":"{{ReleaseManifest.FileName}}","browser_download_url":"{{Base}}{{ReleaseManifest.FileName}}","size":300}""");
            assets.Add($$"""{"name":"{{ReleaseManifest.SignatureFileName}}","browser_download_url":"{{Base}}{{ReleaseManifest.SignatureFileName}}","size":100}""");
        }
        _routes[GitHubRelease.LatestUrl] = () => Ok(Encoding.UTF8.GetBytes($$"""{"tag_name":"v1.0.1","draft":false,"prerelease":false,"assets":[{{string.Join(",", assets)}}]}"""));
        _routes[Base + installerName] = () => Ok(_installer);
    }

    [Fact]
    public void Releases_parse_and_only_github_hosts_over_https_count()
    {
        var json = """
        {"tag_name":"v2.0.0","assets":[
          {"name":"a.exe","browser_download_url":"https://github.com/o/r/releases/download/v2.0.0/a.exe","size":5},
          {"name":"evil.exe","browser_download_url":"https://evil.example/a.exe","size":5},
          {"name":"plain.exe","browser_download_url":"http://github.com/o/r/a.exe","size":5}]}
        """;
        var r = GitHubRelease.Parse(Encoding.UTF8.GetBytes(json))!;
        Assert.Equal("v2.0.0", r.Tag);
        Assert.Equal(new[] { "a.exe" }, r.Assets.Select(a => a.Name)); // other hosts and plain HTTP are dropped
        Assert.Null(GitHubRelease.Parse(Encoding.UTF8.GetBytes("""{"tag_name":"v2","draft":true,"assets":[]}""")));
        Assert.Null(GitHubRelease.Parse(Encoding.UTF8.GetBytes("""{"tag_name":"v2","prerelease":true,"assets":[]}""")));
        Assert.Null(GitHubRelease.Parse(Encoding.UTF8.GetBytes("not json")));
        Assert.True(GitHubRelease.Allowed(new Uri("https://objects.githubusercontent.com/x")));
        Assert.False(GitHubRelease.Allowed(new Uri("https://github.com.evil.example/x")));
    }

    [Fact]
    public async Task No_release_yet_or_one_without_a_signed_manifest_offers_nothing()
    {
        Assert.False((await Client().CheckAsync(Running, Now, default)).IsOffer); // 404: no release yet
        Publish(withManifest: false);
        var check = await Client().CheckAsync(Running, Now, default);
        Assert.Equal(UpdateVerdict.Untrusted, check.Verdict);
    }

    [Fact]
    public async Task A_release_signed_by_another_key_is_refused()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish(signer: attacker); // what someone holding the GitHub account could do at best
        var check = await Client().CheckAsync(Running, Now, default);
        Assert.Equal(UpdateVerdict.Untrusted, check.Verdict);
        Assert.Null(await Client().DownloadAsync(check, _folder, default));
    }

    [Fact]
    public async Task A_signed_newer_release_is_offered_and_its_installer_kept_only_when_it_matches()
    {
        Publish();
        var client = Client();
        var check = await client.CheckAsync(Running, Now, default);
        Assert.True(check.IsOffer);
        Assert.Equal("1.0.1", check.Manifest!.Version);

        var path = await client.DownloadAsync(check, _folder, default);
        Assert.NotNull(path);
        Assert.Equal(_installer, File.ReadAllBytes(path!));

        // The file on GitHub swapped after signing: same name and size, other bytes.
        File.Delete(path!);
        _routes[Base + "DeadlockScout-Setup-1.0.1.exe"] = () => Ok(Encoding.UTF8.GetBytes("pretend this is an INSTALLER"));
        Assert.Null(await client.DownloadAsync(check, _folder, default));
        Assert.Empty(Directory.GetFiles(_folder)); // nothing left behind, not even the partial file

        // Larger than signed: the download stops at the signed size.
        _routes[Base + "DeadlockScout-Setup-1.0.1.exe"] = () => Ok(_installer.Concat(new byte[1000]).ToArray());
        Assert.Null(await client.DownloadAsync(check, _folder, default));
    }

    [Fact]
    public async Task A_download_redirected_off_githubs_hosts_is_refused()
    {
        Publish();
        var client = Client();
        var check = await client.CheckAsync(Running, Now, default);
        _routes[Base + "DeadlockScout-Setup-1.0.1.exe"] = () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_installer), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/setup.exe") };
        Assert.Null(await client.DownloadAsync(check, _folder, default));
    }

    [Fact]
    public async Task The_running_version_or_an_older_one_is_not_offered()
    {
        Publish();
        Assert.Equal(UpdateVerdict.UpToDate, (await Client().CheckAsync(new Version(1, 0, 1), Now, default)).Verdict);
        Assert.Equal(UpdateVerdict.Downgrade, (await Client().CheckAsync(new Version(1, 1, 0), Now, default)).Verdict);
    }
}
