using System.Net;
using DeadlockScout.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Updates;

/// <summary>What a check found: the verdict on the latest release, and when it is an offer, the verified manifest and its installer.</summary>
public sealed record UpdateCheck(UpdateVerdict Verdict, ReleaseManifest? Manifest = null, ReleaseAsset? Installer = null, string? Note = null)
{
    public bool IsOffer => Verdict == UpdateVerdict.Offer && Manifest is not null && Installer is not null;
}

/// <summary>
/// Talks to GitHub's releases for the updater: reads the latest release, fetches its signed manifest and signature
/// (small, capped), and judges them with <see cref="UpdatePolicy"/> against the keys built into the app. Downloads the
/// installer only when asked, and keeps it only if it matches the signed size and SHA-256. Nothing GitHub says is
/// trusted on its own; every file comes over HTTPS from GitHub's own hosts, and the signature decides.
/// </summary>
public sealed class UpdateClient
{
    private readonly HttpClient _http;
    private readonly IReadOnlyCollection<string> _keys;
    private readonly ILogger _log;

    public UpdateClient(HttpClient http, IReadOnlyCollection<string>? keys = null, ILogger<UpdateClient>? log = null)
    {
        _http = http;
        _keys = keys ?? UpdateTrust.TrustedKeys.ToList();
        _log = log ?? NullLogger<UpdateClient>.Instance;
    }

    public async Task<UpdateCheck> CheckAsync(Version running, DateTimeOffset now, CancellationToken ct)
    {
        var latest = await GetSmallAsync(new Uri(GitHubRelease.LatestUrl), 1024 * 1024, ct, json: true).ConfigureAwait(false);
        if (latest is null) return new UpdateCheck(UpdateVerdict.UpToDate, Note: "no release yet");
        var release = GitHubRelease.Parse(latest);
        if (release is null) return new UpdateCheck(UpdateVerdict.Invalid, Note: "latest release unreadable");
        if (release.Asset(ReleaseManifest.FileName) is not { } manifestAsset || release.Asset(ReleaseManifest.SignatureFileName) is not { } sigAsset)
            return new UpdateCheck(UpdateVerdict.Untrusted, Note: $"{release.Tag} has no signed manifest");

        var manifest = await GetSmallAsync(manifestAsset.Url, GitHubRelease.MaxSmallFile, ct).ConfigureAwait(false);
        var sigText = await GetSmallAsync(sigAsset.Url, GitHubRelease.MaxSmallFile, ct).ConfigureAwait(false);
        if (manifest is null || sigText is null) return new UpdateCheck(UpdateVerdict.Untrusted, Note: $"{release.Tag}'s manifest could not be fetched");
        var signature = UpdateTrust.ReadSignature(System.Text.Encoding.ASCII.GetString(sigText)) ?? Array.Empty<byte>();

        var (verdict, m) = UpdatePolicy.Evaluate(manifest, signature, _keys, running, now);
        if (verdict != UpdateVerdict.Offer || m is null) return new UpdateCheck(verdict, m, Note: release.Tag);
        if (release.Asset(m.Installer) is not { } installer) return new UpdateCheck(UpdateVerdict.Invalid, m, Note: $"{m.Installer} is not attached to {release.Tag}");
        return new UpdateCheck(UpdateVerdict.Offer, m, installer);
    }

    /// <summary>
    /// The offered installer saved in <paramref name="folder"/>, or null when it could not be fetched or does not match
    /// the signed size and SHA-256 (then nothing is left behind). Never reads past the signed size.
    /// </summary>
    public async Task<string?> DownloadAsync(UpdateCheck offer, string folder, CancellationToken ct)
    {
        if (!offer.IsOffer) return null;
        var m = offer.Manifest!;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(m.Installer));
        var partial = path + ".partial";
        try
        {
            using (var response = await _http.GetAsync(offer.Installer!.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode || !GitHubRelease.Allowed(response.RequestMessage?.RequestUri)) return null;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = File.Create(partial);
                if (!await CopyCappedAsync(source, target, m.Size, ct).ConfigureAwait(false)) return null;
            }
            bool matches;
            await using (var check = File.OpenRead(partial)) matches = UpdatePolicy.InstallerMatches(m, check);
            if (!matches) { _log.LogWarning("Update {Version}: the installer does not match the signed hash; discarded", m.Version); return null; }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static async Task<bool> CopyCappedAsync(Stream source, Stream target, long max, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > max) return false;
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return total == max;
    }

    /// <summary>A small file's bytes, or null on 404, a refused host or one over <paramref name="max"/> bytes.</summary>
    private async Task<byte[]?> GetSmallAsync(Uri url, long max, CancellationToken ct, bool json = false)
    {
        if (!GitHubRelease.Allowed(url)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (json) request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (!GitHubRelease.Allowed(response.RequestMessage?.RequestUri)) return null; // a redirect off GitHub's hosts
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
