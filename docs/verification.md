# Verification

Results from 2026-11-04 on Windows 11 x64 (.NET SDK 10.0.402, Intel Core 6th generation, 8 logical cores). Downloads and test data were on a USB hard disk (about 37 MB/s sequential).

## Tests

`dotnet test tests/IDMRea.Core.Tests -c Release`: 200 passed on `net10.0` and 7 on `net10.0-windows`, none failed. Engine tests also run on Linux in CI.

They cover, against a local HTTP server with switchable faults:

- Exact bytes for single and multi-part downloads, dynamic splitting, a server that ignores ranges, a busy server (fewer parts), a dropped part, a changed file, bad `Content-Range`, truncated bodies, and empty and tiny files.
- Pause, crash and restart: each part resumes from its saved offset, and saved progress never counts bytes that aren't on disk.
- A resumed download behind an expiring signed redirect gets a fresh link from its original URL.
- Not enough disk space fails before downloading; a resume needs space only for what is left.
- The finished file carries the internet zone mark (`Zone.Identifier`, ZoneId=3) without any user name or password from the link.
- Publishing never overwrites a file. After a crash, the finished file is recognised by its Windows file ID (which survives the move into place), an unrelated file of the same size is left alone, a name taken by another file and a vanished file each fail with a plain reason, and Retry downloads a vanished file again.
- Retries, `Retry-After`, the shared speed limit, the queue and the start-up rule.
- The pipe protocol (versions 1 and 2, `subscribe` pushes) and a server that takes 21 s to answer a link check.
- Settings, file names, categories, sidebar counts, search (Arabic-Indic digits search as 0-9) and the drawn icon.
- Browser downloads: the referrer, cookies and user agent reach the server on the check and on every part; cookies are never stored in plain text and survive a restart; a download without a folder goes to its type's folder; the SHA-256 check (a match is marked verified, a mismatch fails with a plain reason and Retry downloads again).
- The native host: its framing, the folder it chooses, and an `add` that goes through the engine; the registry keys and manifest "Add to browser" writes.
- The tray menu: status line, which actions are enabled, the ticked speed limit (including one typed in Settings), and what each item does. A speed limit changed inside the engine is pushed to an open app.

The Recycle Bin test runs only with `IDMREA_TEST_RECYCLE=1`, because each run leaves its test file in the real Recycle Bin. CI sets it.

## The browser extension

- `npm test` in `src/IDMRea.Extension`: 30 tests of the rules (file or page, warnings, list parsing, link candidates, sizes and checksums in the page text, the tab, redirect and layer rules). Each rule was also broken on purpose, one at a time (46 changes), and every change turned a test red.
- `node scripts/check-extension.mjs`: Playwright's Chromium, headless, with the extension, a real native host and a real engine on their own data folder, against the fixture's trap pages. All 20 checks passed: the files found with type, size and checksum; the fake 4.2 GB installer called out; the ad link left out; the toolbar count; an invisible layer and an empty layer taken away with the link under them working; no tab from a click on empty space; only the mirror from a link to it; a fake button's ad tab closed; an ad redirect sent back; a malware-listed site blocked and "Continue anyway"; a browser download taken over, checked against the page's checksum, saved with the right bytes and gone from the browser. Breaking the layer, new-tab and redirect guards at once turned exactly those three checks red.

## The real app

`scripts/capture-windows.ps1` runs the published app and engine against `scripts/fixture-server.py` and drives the window through UI Automation. It uses its own data folder (`artifacts\capture-data`), so a copy of IDM-Rea already running on another folder keeps its window, engine and downloads. It passed. It checks:

- The window, sidebar, toolbar and search are present, in light and dark.
- A missing link is refused when added, with "no longer has this file".
- A link that disappears after the check ends in Failed; a connection cut halfway still completes with every byte; a large file runs in more than one part.
- Each list (Downloading, Queued, Completed, Failed, Documents) shows the right downloads.
- The Add dialog fills in name, size and type, and the download lands in the per-type folder.
- Settings reach the engine; under Ultra performance, parts per file shows 16 and can't be edited.
- Closing the window exits the app but leaves the engine downloading in the tray; reopening shows the same downloads.
- The engine is per-monitor DPI aware, so the tray icon and menu are drawn at the screen's real scale.

The tray menu was also opened on this PC and checked by eye: dark like Windows, status line, bold "Open IDM-Rea", "Pause all" greyed when nothing runs.

Screenshots and `runtime-verification.json` are saved in `artifacts\screenshots`.

## CPU and memory

`scripts/measure-resources.ps1`, three 512 MB downloads from the local server at once:

| Phase | CPU (% of one core) | Memory at end |
|---|---|---|
| Engine idle | 0.16 % | 13.1 MB |
| 3 downloads, 48 MB/s | 12.52 % (6.3 % kernel) | 67 MB |
| Engine idle after | 0.93 % | 10.5 MB |
| App open and idle | 0.39 % | 161.6 MB |

The downloads cost 2.61 ms of CPU per MB, about 3.3 % of one core at a 100 Mbit/s line. The script still reports the 10 % target as missed, because it compares the average while the files arrive at 48 MB/s from the local server.

Three problems found while measuring were fixed:

- **Far-off writes waited for NTFS.** Writing a part far into a pre-sized file made Windows zero-fill everything before it (9.6 s for a write at 448 MB on the USB disk). Part files are now sparse while downloading. Three 512 MB downloads went from 1.4 MB/s to 40 MB/s at the start.
- **A SHA-256 of every finished file** cost about 5.5 ms of CPU per MB on this processor (no SHA instructions), out of 8.08 ms per MB in all. It was used only to recognise the file after a crash, and nothing showed it, so the Windows file ID does that job now. The same three files finished in 32 s instead of 114 s.

In the first 30 s after the window opens, .NET start-up alone uses about 15 % of one core; the app-idle phase starts after that.

## Not checked here

- Notifications on screen, and every tray menu item clicked by hand.
- A live change of the Windows accent colour (changing it is a system setting).
- A signed installer, which doesn't exist yet.
- The extension in branded Chrome and Edge by hand (they no longer load extensions from the command line, so the check uses Playwright's Chromium), and on real download sites.
