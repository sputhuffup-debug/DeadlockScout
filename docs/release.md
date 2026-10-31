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
5. **The build itself.** Every NuGet package, direct and transitive, is pinned by version and content hash in each
   project's `packages.lock.json`, and CI restores in locked mode, so a changed or swapped package fails the build.
   `nuget.config` allows nuget.org only, for every package id (no dependency confusion through another feed). GitHub
   Actions are pinned to commit SHAs, not tags a compromised Action could move. Each workflow's token is read-only
   unless a job must write: the release job may only create the draft, the screenshots job only push images to its
   own branch, and checkouts that do not push keep no git credentials. Vulnerable packages fail the build (NuGet's
   audit, as errors), and Dependabot proposes updates to the pinned packages and Actions each week, as pull requests
   that go through the same CI. The release workflow only drafts, with a signed provenance for its installer; the
   owner checks that provenance, then signs and publishes. Even a build that got past all of this cannot ship:
   installed copies run only what the owner's key signed, and the owner signs only an installer whose provenance
   shows this repository's release workflow built it.

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
the installer replaces the app and starts it again. The tray's **Check for updates** runs the same check at once,
between matches and even with the automatic checks off, and shows the answer in a balloon (`Core/Updates/ManualCheck`);
scheduled and asked-for checks never overlap. Code: `Api/Updates/UpdateClient.cs`, `App/Services/UpdateService.cs`.

## The trusted keys

Pinned in `UpdateTrust.TrustedKeys` since Oct 31, 2026 (P-256; fingerprints are the SHA-256 of the key's DER):

| Key | Fingerprint |
|---|---|
| signing | `E91B FCE3 DFE9 7A88 93CA F816 0D8C 7016 9B28 FECC 6189 11EB 8C35 33D0 5D9D 79B1` |
| backup (offline) | `7C4C 4599 300F 36C3 B711 EB5A 61F9 4598 8314 AB50 646B 6527 1A01 13B6 8B84 C492` |

The same fingerprints are published off GitHub by the owner; if the two ever differ, trust the one off GitHub.

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
2. The release workflow builds the installer, attests its provenance (a Sigstore-signed statement that this
   repository's `release.yml`, at the tagged commit, built a file with this SHA-256) and drafts a GitHub release with
   it. Work on that draft: do not create a second release for the tag.
3. Download the installer from the draft into a folder outside the repository and check where it came from, with the
   GitHub CLI (`winget install --id GitHub.cli`, then `gh auth login` once):
   ```
   gh attestation verify DeadlockScout-Setup-1.2.3.exe -R sputhuffup-debug/DeadlockScout --signer-workflow sputhuffup-debug/DeadlockScout/.github/workflows/release.yml --source-ref refs/tags/v1.2.3
   ```
   It must print `Verification succeeded!`. Anything else means the file in the draft is not the one the workflow
   built for that tag: delete the draft and do not sign.
4. Sign it on your PC:
   ```
   dotnet run --project tools/DeadlockScout.ReleaseSign -- sign <signing.private.pem> DeadlockScout-Setup-1.2.3.exe 1.2.3 --notes <release url>
   dotnet run --project tools/DeadlockScout.ReleaseSign -- verify <folder with the three files>
   ```
5. Upload `release-manifest.json` and `release-manifest.json.sig` to the draft and publish it.

A release whose manifest expires (90 days) stays installable by hand; re-signing it with a later expiry is step 4
again. Never sign an installer you did not build or download from your own draft.

## Account hygiene (owner)

- Two-factor authentication on GitHub with a hardware key or passkey, not SMS.
- Branch protection on `main` (no force-push, no deletion) and tag protection for `v*`.
- No long-lived personal access tokens with write access; fine-grained tokens with expiry when one is needed.
- Review the repository's Actions permissions (read-only by default) and its installed GitHub Apps now and then.
