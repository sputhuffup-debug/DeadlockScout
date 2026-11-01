# Development

For people building DeadlockScout from source or working on it. Players start at the [README](../README.md).

## Building

Requires the .NET 10 SDK (10.0.100 or later; `global.json` pins the 10.0 band). `dotnet build` and `dotnet test` work
on Windows, Linux and macOS: the WPF app builds everywhere with `EnableWindowsTargeting`, and runs on Windows 10
(2004 or later) and 11.

```
dotnet build
dotnet test
dotnet run --project src/DeadlockScout.App     # Windows
dotnet publish src/DeadlockScout.App -c Release -r win-x64 --self-contained false   # the distributable
```

CI builds and tests every push, and publishes the win-x64 build as the `DeadlockScout-win-x64` artifact of each run.

Packages are pinned in each project's `packages.lock.json`, and CI restores in locked mode (`CI=true`, which GitHub
sets). After adding or bumping a `PackageReference`, run `dotnet restore` (locally, without `CI` set) and commit the
changed lock files with it; otherwise CI fails with NU1004. Packages come from nuget.org only (`nuget.config`).
Workflow Actions are pinned to commit SHAs with the tag in a comment; to bump one, take the new tag's commit from
`git ls-remote https://github.com/actions/<name> refs/tags/<tag>` and change both. Dependabot (`.github/dependabot.yml`)
proposes those bumps and our NuGet updates weekly, a week after each release, as pull requests through the same CI;
the vendored DemoFile's packages are left to its `VENDORED.md`. Restore audits every package against nuget.org's
advisories: a moderate or worse one fails the build (NU1902-NU1904), so update or replace it before merging.
`CLAUDE.md` has the layout of the solution and the rules every project follows; `docs/architecture-seed.md` is the
founding design document, including the table of every cached API call.

## Where things live

Under `%LOCALAPPDATA%\DeadlockScout`: `settings.json` (every setting, written in full on first run), `cache.db` (the
API cache), and `logs\scout-yyyy-MM-dd.log` (the app's log, a week kept). An API key for deadlock-api.com is
optional; `api.apiKey` raises your quota.

## The live match feed

By default (`api.liveSource: "direct"`) the app reads Valve's delayed public broadcast itself with the
[DemoFile](https://github.com/saul/demofile-net) parser, vendored with fixes for the current patch (see
`third_party/demofile-net/VENDORED.md`). Only the broadcast address comes from deadlock-api. If a game patch breaks
parsing, the app switches that match to deadlock-api's hosted live query automatically.

- `"hosted"` uses the hosted live query from the start.
- **Settings → Live feed and API** switches between Direct, Hosted and Self-hosted and writes this setting. A change
  applies from the next match.
- `"selfHosted"` uses the deadlock-api live-events container you run yourself, expected at `http://localhost:3000`
  (`api.liveUrl`). Setup guide: [live-events README](https://github.com/deadlock-api/deadlock-api/tree/master/live-events);
  image: [ghcr.io/deadlock-api/deadlock-live-events](https://github.com/orgs/deadlock-api/packages/container/package/deadlock-live-events).
  ```
  docker run -d --restart unless-stopped -p 3000:3000 ghcr.io/deadlock-api/deadlock-live-events:latest
  ```
- The app writes every setting into `settings.json` on first run, so an install from before the Direct default keeps
  `"liveSource": "hosted"` until you change it with the switch or in the file.

## After a game patch

To check that the parser understands the current patch, run it on one of your replays or on a live broadcast URL (it
also lists every objective change it reads, which is how the mid boss and urn reads get confirmed):

```
dotnet run --project tools/DeadlockScout.ReplayCheck -- path\to\match.dem
```

The patch check runs the whole checklist at once: what changed in the build, heroes and items since its last run, the
benchmarks' patch key, the laning timers' spawn defaults and, given a replay, the ReplayCheck run above. It exits 1
when something changed.

```
dotnet run --project tools/DeadlockScout.PatchCheck -- --replay path\to\match.dem
```

Replays hold every player's name and account id: they are gitignored and never committed.

## Installer

`installer/DeadlockScout.iss` (Inno Setup 6) packages the published app; `installer/build.ps1 -Publish publish -Out
installer-out` builds it on Windows, and CI builds it on every push (the **DeadlockScout-Setup** artifact). A tag
`v1.2.3` matching `<Version>` in `Directory.Build.props` runs `.github/workflows/release.yml`, which tests, builds the
installer and drafts a release with it and `SHA256SUMS.txt`; nothing is published until the owner signs it.
`build.ps1` finds Inno Setup 6 under Program Files or `%LOCALAPPDATA%\Programs` (where a per-user install puts it)
and only falls back to Chocolatey where it exists (CI). Players whose Smart App Control blocks the downloaded
installer can build their own from a release tag:
[troubleshooting](troubleshooting.md#build-the-installer-yourself).

## Releases

How releases are built, signed with the owner's offline key and checked by installed copies, and why a compromised
GitHub account cannot push an update: [release.md](release.md).

## Screenshots

The screenshots in the README and `docs/features.md` use the app's demo lobby, with made-up player names, and never
show game art. The Screenshots workflow renders them on a Windows runner (`DeadlockScout --screenshots <folder>`)
whenever a branch changes the app, and commits them to that branch; pull before pushing again, and look at them
before merging.

## Status and verification

Pre-alpha. The solution builds on Windows and Linux and the logic is covered by unit tests. It has been run through real matches: match detection from console.log (start, end screen, leaving, starting the app mid-match), the Direct broadcast feed for a whole match, scouting, and the overlay over the game. [`docs/verification/api.md`](verification/api.md) records what was checked against the API's source and real matches, what the design doc got wrong, and what is still open. [`docs/roadmap.md`](roadmap.md) is the plan to 1.0, milestone by milestone.
