# Releases and updates

How a release is built, signed and delivered, and why a compromised GitHub account cannot push malware to installed
copies. The installer and the in-app updater build on this; see the roadmap's M11 installer line for their state.

## The threat

Installed copies download updates from the repository's GitHub releases. Anyone who takes over the GitHub account,
the repository, a maintainer's token or a GitHub Actions workflow can upload files to a release. HTTPS does not help:
the attacker *is* the server. So the app never trusts a release because it is on GitHub.

## What protects players

1. **Releases are signed with a key that never touches GitHub.** Each release carries `release-manifest.json`
   (product, version, publish and expiry dates, installer file name, its SHA-256 and size) and
   `release-manifest.json.sig`, an ECDSA P-256 signature of the manifest's exact bytes. The private key lives on the
   owner's PC, encrypted with a passphrase. The public keys are compiled into the app (`UpdateTrust.TrustedKeys`).
   The updater refuses a manifest no trusted key signed, and an installer whose size or hash differs from it, before
   anything runs. An attacker with the whole GitHub account can upload any file but cannot sign it.
2. **No downgrades, no replays.** A signed manifest is refused when its version is not newer than the running one
   (no pushing players back to a release with a known bug) and after its expiry date, 90 days by default (no holding
   players on an old release by serving an old manifest).
3. **Two keys.** The app trusts the signing key and a backup key kept apart from it (another drive, offline). Either
   signs the update that replaces the other, so a lost or leaked key is recoverable without asking players to
   reinstall by hand.
4. **The player approves each update.** The tray offers it between matches; nothing installs on its own.
5. **The build itself** (roadmap): NuGet lock files with locked restores, GitHub Actions pinned to commit SHAs, the
   smallest workflow permissions, and a release workflow that only drafts a release; the owner signs and publishes.

What this does not cover: someone downloading DeadlockScout for the first time from a compromised GitHub. For that,
publish the public keys' fingerprints somewhere other than GitHub (a profile, a site, a pinned post) so they can be
checked, and sign the installer with an Authenticode certificate once there is one (Windows then names the
publisher instead of warning about an unknown one).

## The updater

An installed copy (one under `%LOCALAPPDATA%\Programs\DeadlockScout`; a build run from a folder never updates) asks
GitHub for the latest release two minutes after it starts and then every 6 hours, never during a match, unless
Settings → Updates is off. It fetches only the small manifest and signature first (capped at 64 KB, HTTPS, GitHub's
own hosts) and offers the release in the tray ("Update to 1.2.3") only when `UpdatePolicy` accepts it. A release it
refuses is logged as a warning, since it is either a mistake or someone else's upload. On the player's click, between
matches, it downloads the installer to `%LOCALAPPDATA%\DeadlockScout\updates`, reading no more than the signed size,
keeps it only if the SHA-256 matches, runs it with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH` and exits;
the installer replaces the app and starts it again. Code: `Api/Updates/UpdateClient.cs`, `App/Services/UpdateService.cs`.

## One-time setup (owner)

On your own PC, from the repository:

```
dotnet run --project tools/DeadlockScout.ReleaseSign -- keygen <folder outside the repo> signing
dotnet run --project tools/DeadlockScout.ReleaseSign -- keygen <another drive or a USB stick> backup
```

Each asks for a passphrase (12 characters or more) and prints the key's fingerprint. Then:

- Back up `signing.private.pem` (it is encrypted) and its passphrase; keep `backup.private.pem` offline.
- Put the two `*.public.pem` contents into `UpdateTrust.TrustedKeys` in a pull request. Public keys are safe to share.
- Publish both fingerprints somewhere other than GitHub.
- `.gitignore` refuses `*.private.pem`; never commit, paste or upload a private key anywhere.

## Each release (owner)

1. Bump `<Version>` in `Directory.Build.props`, merge, and tag the merge `v1.2.3`.
2. The release workflow builds the installer and drafts a GitHub release with it.
3. Download the installer from the draft and sign it on your PC:
   ```
   dotnet run --project tools/DeadlockScout.ReleaseSign -- sign <signing.private.pem> DeadlockScout-Setup-1.2.3.exe 1.2.3 --notes <release url>
   dotnet run --project tools/DeadlockScout.ReleaseSign -- verify <folder with the three files>
   ```
4. Upload `release-manifest.json` and `release-manifest.json.sig` to the draft and publish it.

A release whose manifest expires (90 days) stays installable by hand; re-signing it with a later expiry is step 3
again. Never sign an installer you did not build or download from your own draft.

## Account hygiene (owner)

- Two-factor authentication on GitHub with a hardware key or passkey, not SMS.
- Branch protection on `main` (no force-push, no deletion) and tag protection for `v*`.
- No long-lived personal access tokens with write access; fine-grained tokens with expiry when one is needed.
- Review the repository's Actions permissions (read-only by default) and its installed GitHub Apps now and then.
