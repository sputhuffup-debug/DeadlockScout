using System.Diagnostics;
using DeadlockScout.Api.Http;
using DeadlockScout.Api.Updates;
using DeadlockScout.Api.Work;
using DeadlockScout.Core.Settings;
using DeadlockScout.Core.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// The in-app updater (docs/release.md). A couple of minutes after start and then every 6 hours, never during a match
/// (a check that falls in one runs a few minutes after it ends; <see cref="UpdateSchedule"/>), it asks <see cref="UpdateClient"/> about GitHub's latest release; a release whose manifest one of the built-in keys
/// signed, newer than this one and not expired, becomes an offer in the tray. Only on the player's click, and only
/// between matches, it downloads the installer, keeps it only if it matches the signed hash, runs it silently and lets
/// the app exit; the installer starts the new version. A copy run from a build folder (not installed) never updates.
/// The tray's "Check for updates" runs the same check at once (<see cref="CheckNowAsync"/>), even with the automatic
/// checks switched off, and says what it found.
/// </summary>
public sealed class UpdateService : BackgroundService
{

    private readonly UpdateClient _client;
    private readonly MatchActivityGate _gate;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger<UpdateService> _log;
    private readonly SemaphoreSlim _checking = new(1, 1);

    public UpdateService(UpdateClient client, MatchActivityGate gate, IOptionsMonitor<ScoutSettings> settings, ILogger<UpdateService> log)
    {
        _client = client;
        _gate = gate;
        _settings = settings;
        _log = log;
    }

    /// <summary>When the last check ran (scheduled or asked for) and what it found, for Diagnostics; null before the first.</summary>
    public (DateTimeOffset At, string Result)? LastCheck { get; private set; }

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
        var schedule = new UpdateSchedule(Task.Delay, () => _gate.IsMatchLive, ct2 => _gate.WaitAsync(RequestPriority.Background, ct2),
            () => _settings.CurrentValue.Updates.Check);
        try
        {
            await schedule.RunAsync(async (putOff, ct2) =>
            {
                if (putOff) _log.LogInformation("Update check: put off by a match, running now that it is over");
                await CheckAsync(ct2).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// A check the player asked for, from the tray: runs now unless a match is live or this copy is not installed, and
    /// returns what to tell them. A newer signed release becomes the tray's offer exactly as from a scheduled check.
    /// </summary>
    public async Task<(string Title, string Text)> CheckNowAsync(CancellationToken ct)
    {
        if (!IsInstalled) return ManualCheck.NotInstalled;
        if (_gate.IsMatchLive) return ManualCheck.DuringMatch;
        _log.LogInformation("Update check asked for from the tray");
        var check = await CheckAsync(ct).ConfigureAwait(false);
        return ManualCheck.Describe(check?.Verdict, Running.ToString(3), check?.Manifest?.Version);
    }

    /// <summary>One check, logged; null when GitHub could not be reached. Scheduled and asked-for checks never overlap.</summary>
    private async Task<UpdateCheck?> CheckAsync(CancellationToken ct)
    {
        await _checking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var check = await _client.CheckAsync(Running, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            if (check.IsOffer)
            {
                bool newer = Offer?.Manifest is not { } prev || Version.Parse(check.Manifest!.Version) > Version.Parse(prev.Version);
                Offer = check;
                LastCheck = (DateTimeOffset.UtcNow, $"{check.Manifest!.Version} available");
                _log.LogInformation("Update {Version} is available (signed, valid until {Expires:yyyy-MM-dd})", check.Manifest!.Version, check.Manifest.Expires);
                if (newer) OfferFound?.Invoke(check);
            }
            else if (check.Verdict is UpdateVerdict.Untrusted or UpdateVerdict.Invalid or UpdateVerdict.Expired or UpdateVerdict.Downgrade)
            {
                // Worth a line: a release on GitHub that the app refuses is either a mistake or someone else's upload.
                _log.LogWarning("Latest release refused: {Verdict} ({Note})", check.Verdict, check.Note);
                LastCheck = (DateTimeOffset.UtcNow, $"latest release refused ({check.Verdict})");
            }
            else
            {
                // One line per check, so the log answers "did it look, and what did it find?" on its own.
                _log.LogInformation("Update check: {Running} is up to date ({Note})", Running, check.Note ?? "latest release");
                LastCheck = (DateTimeOffset.UtcNow, "up to date");
            }
            return check;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Update check failed; trying again later");
            LastCheck = (DateTimeOffset.UtcNow, "could not reach GitHub");
            return null;
        }
        finally { _checking.Release(); }
    }

    /// <summary>
    /// Downloads and checks the offered installer, then starts it silently; true when it started (the app should exit
    /// so the installer can replace it). False during a match, without an offer, or when the download does not match.
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken ct)
    {
        if (_gate.IsMatchLive || Offer is not { IsOffer: true } offer) return false;
        using var installer = await _client.DownloadAsync(offer, DownloadFolder, ct).ConfigureAwait(false);
        if (installer is null) return false;
        _log.LogInformation("Installing update {Version}", offer.Manifest!.Version);
        // Still held from its hash check: Windows maps it for the new process, and only then is it let go.
        Process.Start(new ProcessStartInfo(installer.Path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH") { UseShellExecute = false });
        return true;
    }
}
