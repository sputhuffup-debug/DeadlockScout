# 0.2 checks on the owner's PC

The 0.2 roadmap's "Checks on the owner's PC" and its gate, as steps. Run them on an installed build that has the 0.2
fixes (main window to the tray, OCR calibration check, the guard on real time, the update after a match, the encrypted
API key, crate timers). Each check says what to send back. Send only what it names: logs, replays and Copy
diagnostics are scrubbed in different ways, and a replay or the full log holds every player's name and account id.

Roughly in the order that costs least. One match covers checks 1, 2, 5 and 8 at once.

## Before a match (no game needed)

1. **API key encrypted.** With a key set in Settings → Live feed and API, open `%LOCALAPPDATA%\DeadlockScout\settings.json`.
   `api.apiKey` should be `null`, and `api.apiKeyProtected` should start with `dpapi1:`. *Send:* yes or no for each, never the values.
2. **Main window memory.** Open Diagnostics from the tray and read "Memory now" with the main window open. Close
   the main window, wait 10 s, and read it again; it should say "main window released". Then press Ctrl+Shift+D:
   the window comes back as it was. The log has a line "Main window closed to the tray: memory X MB -> Y MB".
   *Send:* the two Memory now lines and the log line.
3. **Steam content log.** In Steam → Deadlock → Properties → Updates, set automatic updates off, then back on. Copy the
   new lines at the end of `<Steam>\logs\content_log.txt` (they name the app id, not you). *Send:* those lines.
4. **Uninstall and Smart App Control.** Uninstall from Windows Settings → Apps. Answer the "remove settings" question
   both ways on two runs if you can. Afterwards check that `%LOCALAPPDATA%\Programs\DeadlockScout` is gone (and
   `%LOCALAPPDATA%\DeadlockScout` too when you chose to remove settings). Then build the installer on the same PC
   (`installer\build.ps1`, see development.md) and run it. *Send:* what was left behind, and whether Smart App
   Control let the self-built installer run.

## During and after a match

5. **OCR calibration.** With the game running in borderless windowed mode, open the calibration tool from the tray
   and leave it on each box for a few seconds. A tight box says "steady" in blue; a box that turns gold names what
   it reads besides the number. *Send:* the line for the souls box and the clock box.
6. **Match ingest.** After the match, open it in Deadlock's in-game match history, then click **Send my matches to
   deadlock-api** in the app's match history. *Send:* what the button said and the "Match ingest" line in Diagnostics.
   If it says nothing was found in Steam's cache, look (files only, no network capture) for a recently written file
   under `<Steam>\appcache\httpcache` and say whether one appeared when you opened the match.
7. **Camp and crate respawns.** The log (Diagnostics → Open log folder) has a line for each respawn the app saw:
   "Camp respawn observed: Small camp back 1:27 after its clear (setting 1:25) …", crates included. *Send:* only
   those lines (`findstr /c:"respawn observed" scout-*.log`). They hold no names or ids.
8. **Direct feed on the current build.** Download a replay of a recent match (any match, no need for yours) and run
   `dotnet run --project tools/DeadlockScout.PatchCheck -- --replay path\to\match.dem`. *Send:* the build line, the
   Replay summary, the Jungle section and the Camps section. Never send the roster above them.

## The 0.2 gate

Three real matches on the installed 0.2 with OCR on. For each one, send the "performance:" line from Diagnostics
under the last match, and note any pace number that looked absurd. The gate passes when:

- no pace number is absurd;
- memory stays under 400 MB;
- camp and crate respawns land within 10 s of the settings (or the settings are corrected from check 7);
- one match is stored through "Send my matches" (check 6).
