# Checks on the owner's PC, before 0.3

0.5.0 is the build to run them on (0.3.0 was the first candidate, 0.3.1 fixed the souls box for the "SOULS" label and
haunted souls; 0.3.2 was never released; 0.3.3 is the last 0.3). 0.5.0 adds OCR boxes that follow the game window to
another shape, colour-blind colours, the rank forecast, match history filters, the share card, the content log and
respawn lines in Copy check results, Ctrl+Shift+P for the overlay preview, and each panel's own size (check 22). 0.4.0 dropped the enemy scoreboard boxes from the
calibration tool and adds the preset boxes, "affordable now" on the next item, the hero pool's pace against the rank
gate, the break nudge on a pace slide and in the tray, and the record with the friends you queue with (check 21).
0.3.3 added one glyph set per box, the soul icon learned and skipped, the
calibration tool's outline and "Teach this box again", clock misreads put right, the haunted souls warning, souls lost
at a death, the spending recap, the tray warning when a box stops reading, and Diagnostics' Copy check results.
Everything 0.3's roadmap lists is in it (the 0.2
fixes, the glyph reader, OCR souls read as unspent souls and turned into live net worth, the team souls lead, the next
item at your pace, your goals and the unspent-souls line during the match, the double-tap hide, a second start
opening the app, the "what's new" notice, settings backup and restore, process hardening). If these checks find
anything else, the fixes ship as 0.4.1 and the checks they touch are run again on it. This sheet is the 0.2 roadmap's
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
   new lines at the end of `<Steam>\logs\content_log.txt` (they name the app id, not you). *Send:* those lines; from
   0.5, Copy check results' "Check 3" line carries them, so with the app running you can paste that instead.
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
   those lines (`findstr /c:"respawn observed" scout-*.log`), or from 0.5 Copy check results' "Check 7" line, which
   carries the ones seen since the app started. They hold no names or ids.
8. **Direct feed on the current build.** Download a replay of a recent match (any match, no need for yours) and run
   `dotnet run --project tools/DeadlockScout.PatchCheck -- --replay path\to\match.dem`. *Send:* the build line, the
   Replay summary, the Jungle section and the Camps section. Never send the roster above them.

## 0.3's features, in the same matches

9. **Glyph reader learns.** In the calibration tool (step 5), keep the match clock box selected for about 10 seconds
   while the clock ticks. The grey line under the reading goes from "learning, still missing …" to "knows every digit,
   and reads this box as “12:34” itself". The souls count has its own font and learns separately (0.3.3): its line
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
17. **"What's new".** Updating from the tray (to 0.5.0) shows "Updated to 0.5.0" in the tray on the first start;
    a click opens its release notes. *Send:* whether it showed.
18. **Back up and restore.** Settings → Back up… saves a `.dsbackup.json`; change a panel or
    move one, then Restore… that file: the layout comes back, and the live feed still works (the API key stayed).
    Open the backup in Notepad and check it holds no `apiKey` value. *Send:* works or not.
19. **Process hardening.** Diagnostics' setup lines say "Process hardening: on: DLL search,
    image loads, extension points, access list". Play one match as usual: the overlay, OCR (and the calibration tool),
    the tray and an update all still work. *Send:* the line, and anything that stopped working (if something did,
    `security.hardenProcess` false in settings.json turns it off until it is fixed).

20. **0.3.3's coaching.** In a match with OCR on: carrying 1,000+ haunted souls, the pace strip warns ("… haunted,
    lost if you die"); after a death that takes them, it says "Died at 14:20: lost 1,051 haunted souls" for half a
    minute; after the match, the last match's card has a "Spending:" line. If a box stops reading for 45 s, the tray
    says "OCR cannot read a box". *Send:* whether each showed, and whether the death loss matched what you lost.

21. **0.4.0's features.** Calibration tool: with a box cleared, **Use the preset boxes** puts it where the count or
    the clock is, and its preview reads it (send your resolution if it misses). In a match: once your unspent souls
    cover your next usual item, the next-item line turns green ("… · affordable now") and goes once you buy it. In the
    main window: the hero pool shows "pace ±N% vs <rank>" for your heroes, and "With friends:" names the friends you
    queue with, with a record that looks right. *Send:* whether each showed, the resolution if the preset missed, and
    whether the friends' names were right (not the names themselves).
22. **0.5.0's features.** Resize or move the game to a window of another shape (or another monitor): the tray says "OCR
    boxes moved" once, and the calibration tool shows the boxes where they now read. Settings → Overlay →
    Colour-blind colours, then restart: ahead/behind are blue and orange everywhere. In the main window: the session
    view's "Rank:" line looks right against your recent ranked games; the match history's filter bar (a hero, Ranked
    only, a friend) changes the cards and the numbers above them; the last match's **Copy as image** pastes into
    Discord with no other player's name. *Send:* whether each worked, the window size if the boxes missed after a
    resize, and Copy check results after a match (it now carries checks 3 and 7 too). Overlay editing: Ctrl+Shift+P
    turns the preview on and off, in game too; in it, the wheel over a panel sizes that panel on its own and Reset to
    default undoes it. *Send:* whether both worked.

## Before 0.3 is released

- Everything above sent back; the fixes it calls for are 0.3's first lines.
- 0.3's own gate (roadmap): three real matches with the glyph reader on, fewer misreads than in 0.2 and no absurd
  pace numbers, every coaching line seen at least once and none wrong against the post-match report.
- A dry run of 1.0's gate on a fresh Windows install (a virtual machine is enough): install 0.5.0, go through the
  first-run wizard, play one match, then update to the next build. *Send:* anything the wizard got wrong and whether
  the update came through.

## The 0.2 gate

Three real matches on the installed 0.2 with OCR on. For each one, send the "performance:" line from Diagnostics
under the last match, and note any pace number that looked absurd. The gate passes when:

- no pace number is absurd;
- memory stays under 400 MB;
- camp and crate respawns land within 10 s of the settings (or the settings are corrected from check 7);
- one match is stored through "Send my matches" (check 6).
