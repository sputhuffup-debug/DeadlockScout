# Checks on the owner's PC, before 0.3

0.3.1 is the build to run them on (0.3.0 was the first candidate; 0.3.1 adds the souls box fix for the "SOULS" label and
haunted souls, verification note 43, so the OCR checks 5, 9 and 10 belong on it). Everything 0.3's roadmap lists is in it (the 0.2
fixes, the glyph reader, OCR souls read as unspent souls and turned into live net worth, the team souls lead, the next
item at your pace, your goals and the unspent-souls line during the match, the double-tap hide, a second start
opening the app, the "what's new" notice, settings backup and restore, process hardening). If these checks find
anything else, the fixes ship as 0.3.2 and the checks they touch are run again on it. This sheet is the 0.2 roadmap's
"Checks on the owner's PC" and its gate, then the checks 0.3's own features need before 0.3 is done. Each check says what to send back. Send only what it names: logs, replays and Copy
diagnostics are scrubbed in different ways, and a replay or the full log holds every player's name and account id.

After a match, Diagnostics' **Copy check results** (0.3.3) copies the lines checks 2, 6, 10 and 19 and the gate ask for
in one block, scrubbed; paste that instead of copying them one by one. Roughly in the order that costs least. One match covers checks 5, 6, 7 and 10–14 at once.

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
   and leave it on each box for a few seconds. Draw the souls box from just right of the soul icon, wide enough for a
   five-digit count and tall enough to cover the number both where it sits and where it moves up to when you carry
   haunted souls (the troubleshooting page says how). From 0.3.1 on the "SOULS" label is ignored and the haunted line
   is read and added; with haunted souls on screen the tool's line ends "plus 1,051 haunted souls". *Send* that line too.
   From 0.3.3 the preview outlines what is read (count blue, skipped soul icon grey, haunted line red): the blue box
   should hold the digits and nothing else; say if it does not. A tight box says "steady" in blue; a box that turns gold names what
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

## 0.3's features, in the same matches

9. **Glyph reader learns.** In the calibration tool (step 5), keep the match clock box selected for about 10 seconds
   while the clock ticks. The grey line under the reading goes from "learning, still missing …" to "knows every digit,
   and reads this box as “12:34” itself". The souls count has its own font and learns separately (0.3.1): its line
   says which of its digits are still missing, and it fills in as your souls change, in the tool or during the match.
   Close the tool (that saves `glyphs.json`). *Send:* the glyph line for the clock and the souls box, how long the
   clock took, and Diagnostics' "Glyph reader:" line after a match (it names each box).
10. **Glyph reader in a match.** After a match, Diagnostics' "Glyph reader:" line says how many reads it answered
    ("ready · 96% of reads, Windows OCR for the rest") and "Memory now" whether the OCR engine loaded. *Send:* both
    lines, and the "OCR: dropped" / "ignored … clock readings" log lines if any (`findstr /c:"OCR:" scout-*.log`).
    Also the "OCR net worth" line (from 0.3.3, after a match where you carried haunted souls, it ends "so the broadcast
    counts them" or "leaves them out": send that part too) (how close your live net worth came to the
    broadcast's, and how many purchases it saw) and the log's last "OCR: … purchases seen" line of the match.
11. **Double tap.** In a match, tap `` ` `` twice quickly: the whole overlay hides; twice again and it is back. Single
    taps and slow pairs do nothing. *Send:* works or not, and whether the console flickering open bothers you (the key
    can be changed in Settings → Overlay).
12. **Team souls lead.** On the enemy pace board, "Souls: Enemy +2.1k, growing · 14:00". Open the scoreboard at about
    that game time (the line is as late as the broadcast, so compare with what the scoreboard showed then, roughly).
    *Send:* whether the leading team and the rough size of the gap matched, and a line that looked wrong if any.
13. **Next item at your pace.** The build hint's "Next: Fortitude ~1:40 at your pace · Oracle 16:30". *Send:* whether
    the countdown felt about right (you bought it around when it said you would have the souls), or a line that looked
    off.
14. **Your goals, live.** First set targets in the match history (laning pace by 10:00, max laning deaths, a phase to
    fix). In the match the pace strip says "Goals: on track for … · one laning death left", then the phase to fix.
    *Send:* whether the deaths count matched yours, and whether "on track" agreed with the 10:00 result.
15. **Second start.** With the app in the tray, start DeadlockScout again from the Start menu: the main window opens,
    still one tray icon. *Send:* works or not.
16. **Unspent souls** (replaces "spent souls in the broadcast's net worth", which your 0.2.1
    logs answered, verification note 42). With OCR on, hold 3,000 souls or more without buying: the pace strip says
    "3.4k unspent: spend before you fight" with the HUD's number, and it goes the moment you buy. *Send:* whether the
    number matched the HUD and went away when you spent.
17. **"What's new".** Updating from the tray (0.3.0 to 0.3.1) shows "Updated to 0.3.1" in the tray on the first start;
    a click opens its release notes. *Send:* whether it showed.
18. **Back up and restore.** Settings → Back up… saves a `.dsbackup.json`; change a panel or
    move one, then Restore… that file: the layout comes back, and the live feed still works (the API key stayed).
    Open the backup in Notepad and check it holds no `apiKey` value. *Send:* works or not.
19. **Process hardening.** Diagnostics' setup lines say "Process hardening: on: DLL search,
    image loads, extension points, access list". Play one match as usual: the overlay, OCR (and the calibration tool),
    the tray and an update all still work. *Send:* the line, and anything that stopped working (if something did,
    `security.hardenProcess` false in settings.json turns it off until it is fixed).

## Before 0.3 is released

- Everything above sent back; the fixes it calls for are 0.3's first lines.
- 0.3's own gate (roadmap): three real matches with the glyph reader on, fewer misreads than in 0.2 and no absurd
  pace numbers, every coaching line seen at least once and none wrong against the post-match report.
- A dry run of 1.0's gate on a fresh Windows install (a virtual machine is enough): install 0.3.1, go through the
  first-run wizard, play one match, then update to the next build. *Send:* anything the wizard got wrong and whether
  the update came through.

## The 0.2 gate

Three real matches on the installed 0.2 with OCR on. For each one, send the "performance:" line from Diagnostics
under the last match, and note any pace number that looked absurd. The gate passes when:

- no pace number is absurd;
- memory stays under 400 MB;
- camp and crate respawns land within 10 s of the settings (or the settings are corrected from check 7);
- one match is stored through "Send my matches" (check 6).
