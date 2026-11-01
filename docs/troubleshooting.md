# Troubleshooting

Find what you see, then what to do. The **Diagnostics** page (main window or tray) shows most of what the app knows:
its Setup section says what it found, and its Match section what happened in your last match.

## Windows blocks the installer

The installer does not carry a code-signing certificate yet (the Authenticode signature Windows uses to name a
publisher), so Windows treats it as a program from an unknown publisher. Updates are protected separately: an
installed copy only installs a release signed with the owner's release key ([release.md](release.md)).

- **"Windows protected your PC" (SmartScreen).** Click **More info**, then **Run anyway**. Or right-click the file →
  Properties → tick **Unblock** → OK, and run it again.
- **"Smart App Control blocked an app that may be unsafe".** Smart App Control has no "run anyway". Your choices,
  safest first:
  1. **Already have DeadlockScout installed?** Update from the tray: **Update to x.y.z**, or **Check for updates**
     first. The app downloads the release itself and runs it only if it carries the owner's signature, and Windows
     does not treat that download like one from your browser.
  2. **Build the installer yourself** (below). Smart App Control mostly blocks programs downloaded from the internet;
     one built on your own PC usually runs. If it is still blocked, only the next choice is left.
  3. **At your own risk, turn Smart App Control off:** Windows Security → App & browser control → Smart App Control
     settings → **Off**. This lowers protection for every program on the PC, not only this one, and on some versions
     of Windows it cannot be turned back on without reinstalling Windows. Check the installer first (below).

### Check a downloaded installer before you override Windows

- Download it only from the repository's [Releases page](https://github.com/sputhuffup-debug/DeadlockScout/releases).
- With the [GitHub CLI](https://cli.github.com/) (`winget install --id GitHub.cli`, then `gh auth login`), check
  that this repository's release workflow built that exact file from the release's tag:
  ```powershell
  gh attestation verify DeadlockScout-Setup-1.2.3.exe -R sputhuffup-debug/DeadlockScout --signer-workflow sputhuffup-debug/DeadlockScout/.github/workflows/release.yml --source-ref refs/tags/v1.2.3
  ```
  It must print **Verification succeeded!** (use the release's own version in place of 1.2.3).
- Without the CLI, `Get-FileHash DeadlockScout-Setup-1.2.3.exe` must match the line in the release's
  `SHA256SUMS.txt`. That catches a damaged download, not a tampered release.

### Build the installer yourself

You need Git, the .NET 10 SDK and Inno Setup 6, once:

```powershell
winget install --id Git.Git -e
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id JRSoftware.InnoSetup -e
```

Then, in a new PowerShell window, in a folder of your choice (use the release's version in place of 1.2.3):

```powershell
git clone https://github.com/sputhuffup-debug/DeadlockScout.git
cd DeadlockScout
git checkout v1.2.3
dotnet publish src/DeadlockScout.App -c Release -r win-x64 --self-contained false -o ..\deadlockscout-publish
powershell -ExecutionPolicy Bypass -File .\installer\build.ps1 -Publish ..\deadlockscout-publish -Out ..\deadlockscout-installer
```

Run `..\deadlockscout-installer\DeadlockScout-Setup-1.2.3.exe`. It installs over an existing copy and keeps your
settings. `git checkout v1.2.3` builds exactly that release; without it you get the newest code under the same
version number. Your installer is not byte-for-byte the release's, which is expected: it is not checked against the
owner's signature, but every update after it is, and those come through the tray. `-ExecutionPolicy Bypass` lets
Windows run the build script for that one command only.

## The app does not start

- **Nothing happens, or Windows says a runtime is missing.** DeadlockScout needs the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) and Windows 10 version 2004 or
  later, or Windows 11. Install the runtime and start it again.
- **It started but there is no window.** It lives in the tray (the icon near the clock; it may be under the ^ arrow).
  Closing the main window only hides it; **Exit** in the window or the tray menu quits.

## The app does not notice my match

The app learns about your match from the game's own log, which Deadlock only writes with the `-condebug` launch option.

- **Diagnostics says "-condebug: NO".** Add it: the setup wizard (tray → Setup wizard) does it for you while Steam is closed,
  or in Steam: Deadlock → Properties → Launch options → type `-condebug`. Then restart Deadlock.
- **It says "Steam: not found" or "Deadlock: not found".** Run the setup wizard (tray → Setup wizard) and type the folders it asks for (the
  Steam folder is the one with `steam.exe` in it).
- **It says "console.log: missing".** Deadlock has not run with `-condebug` yet. Start it once with the option.
- **You started the app in the middle of a match.** That is fine: it reads the game's log back and picks the match up.

## The overlay does not show over the game

- **Deadlock is in exclusive fullscreen.** Nothing can draw over a game in exclusive fullscreen, so the app does not
  try. In Deadlock: Settings → Video → Display mode → **Fullscreen windowed (borderless)**. The app warns you when
  Windows reports the game in exclusive fullscreen, and Diagnostics shows the mode it last saw ("Display mode").
- **You hid it.** Ctrl+Shift+O hides and shows the whole overlay.
- **A panel is missing.** Panels can be turned off one by one in Settings → Overlay, and the lobby panels (scout list,
  lane card) show and hide with Ctrl+Shift+L. **Layout preset → Full** brings every panel back.
- **The panels are in the wrong place or too big.** Click **Preview overlay** in the main window: the panels get gold
  outlines and you can drag them; **Reset to default** puts them back, the slider sets their size.

## The numbers are late or stay empty

- **"Match found, waiting for the broadcast".** Valve's public broadcast of a match starts a minute or so after the
  match does, and the app can only read what it has broadcast. The roster and pace fill in when it starts; Diagnostics
  says how long it took ("Roster: after 1:04").
- **Everything runs 30–60 seconds behind the game.** That is the broadcast's delay, on purpose (it is what keeps the
  information fair). The overlay labels its numbers with the game time they belong to. For your own souls and the
  clock in real time, turn on **OCR** in Settings and draw its boxes once with the calibration tool (tray → Calibrate OCR regions),
  with the game running.
- **OCR reads wrong numbers.** Recalibrate (tray → Calibrate OCR regions, or the button in Settings → OCR) after changing the game's resolution or HUD
  scale. While you are dead the HUD can show a teammate's souls; the app drops those readings (Diagnostics counts them).
- **Nothing at all after the broadcast should have started, right after a game patch.** A patch can change what the
  broadcast looks like. The app falls back to deadlock-api's hosted feed by itself and notes it in Diagnostics ("Feed
  switched from ..."). If it still shows nothing, send Copy diagnostics in a bug report.

## Match history

- **A game you just played is missing, or shows as PROCESSING.** The history comes from deadlock-api.com, which only
  lists a match once it has fetched it from Steam. That usually takes minutes, sometimes hours. The app shows the game
  as a PROCESSING card straight away, asks deadlock-api to fetch it 8 and 60 minutes after the match, and fills the
  card in once it can. If deadlock-api says "Steam is busy", it simply takes longer.
- **"No games yet".** deadlock-api has no stored games for your account yet. Play a match with the app running, or
  wait for the first ones to be fetched.

## Calls fail or are slow

- **Diagnostics shows 429s or rate-limit waits.** The app spaces its calls to stay under deadlock-api's free limits
  and waits when it has to. A free API key from deadlock-api.com (Settings → Live feed and API) raises the limits.
- **Many errors at once.** deadlock-api may be down or busy; the overlay keeps working with what it has cached and
  tries again later. Background work (benchmarks, history) always waits until your match is over.

## Watching someone else's match

Spectating works: the overlay shows every player's pace, tagged A or S for their team, and the lobby stats on
Ctrl+Shift+L. Panels about your own game stay empty, and no report or history card is made for a match you only
watched.

## Starting over

To reset the app, quit it (tray → Exit), then delete `settings.json` (every setting back to its default, and the setup
wizard runs again) and, if you like, `cache.db` (everything fetched is fetched again) from `%LOCALAPPDATA%\DeadlockScout`.

## Reporting a bug

Open **Diagnostics → Copy diagnostics** and paste it into your report, with what you saw and roughly when. It leaves out
account ids, player names, your Windows user name and your API key. **Open log folder** shows the app's full log (one
file a day, the last week kept), but that file can contain account ids and player names: check the lines before you
share them.
