using System.Diagnostics;
using DeadlockScout.Api.Updates;
using DeadlockScout.Api.Work;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// The in-app updater (docs/release.md). A couple of minutes after start and then every 6 hours, never during a match,
/// it asks <see cref="UpdateClient"/> about GitHub's latest release; a release whose manifest one of the built-in keys
/// signed, newer than this one and not expired, becomes an offer in the tray. Only on the player's click, and only
/// between matches, it downloads the installer, keeps it only if it matches the signed hash, runs it silently and lets
/// the app exit; the installer starts the new version. A copy run from a build folder (not installed) never updates.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2), Every = TimeSpan.FromHours(6);

    private readonly UpdateClient _client;
    private readonly MatchActivityGate _gate;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger<UpdateService> _log;

    public UpdateService(UpdateClient client, MatchActivityGate gate, IOptionsMonitor<ScoutSettings> settings, ILogger<UpdateService> log)
    {
        _client = client;
        _gate = gate;
        _settings = settings;
        _log = log;
    }

    /// <summary>A signed, newer release found by the last check; null when there is none.</summary>
    public UpdateCheck? Offer { get; private set; }

    /// <summary>Raised (on a pool thread) when a check finds a newer signed release than the one offered before.</summary>
    public event Action<UpdateCheck>? OfferFound;

    public static Version Running => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Installed by the installer (under %LOCALAPPDATA%\Programs\DeadlockScout), not run from a build folder.</summary>
    public static bool IsInstalled
    {
        get
        {
            var installed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeadlockScout");
            return System.IO.Path.GetFullPath(AppContext.BaseDirectory).StartsWith(System.IO.Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string DownloadFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "updates");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!IsInstalled) { _log.LogInformation("Not an installed copy: updates are off"); return; }
        try
        {
            await Task.Delay(FirstCheck, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (_settings.CurrentValue.Updates.Check && !_gate.IsMatchLive) await CheckAsync(ct).ConfigureAwait(false);
                await Task.Delay(Every, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var check = await _client.CheckAsync(Running, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            if (check.IsOffer)
            {
                bool newer = Offer?.Manifest is not { } prev || Version.Parse(check.Manifest!.Version) > Version.Parse(prev.Version);
                Offer = check;
                _log.LogInformation("Update {Version} is available (signed, valid until {Expires:yyyy-MM-dd})", check.Manifest!.Version, check.Manifest.Expires);
                if (newer) OfferFound?.Invoke(check);
            }
            else if (check.Verdict is UpdateVerdict.Untrusted or UpdateVerdict.Invalid or UpdateVerdict.Expired or UpdateVerdict.Downgrade)
            {
                // Worth a line: a release on GitHub that the app refuses is either a mistake or someone else's upload.
                _log.LogWarning("Latest release refused: {Verdict} ({Note})", check.Verdict, check.Note);
            }
            else
            {
                // One line per check, so the log answers "did it look, and what did it find?" on its own.
                _log.LogInformation("Update check: {Running} is up to date ({Note})", Running, check.Note ?? "latest release");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogInformation(ex, "Update check failed; trying again later"); }
    }

    /// <summary>
    /// Downloads and checks the offered installer, then starts it silently; true when it started (the app should exit
    /// so the installer can replace it). False during a match, without an offer, or when the download does not match.
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken ct)
    {
        if (_gate.IsMatchLive || Offer is not { IsOffer: true } offer) return false;
        var path = await _client.DownloadAsync(offer, DownloadFolder, ct).ConfigureAwait(false);
        if (path is null) return false;
        _log.LogInformation("Installing update {Version}", offer.Manifest!.Version);
        Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH") { UseShellExecute = false });
        return true;
    }
}
