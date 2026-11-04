# Verification

Results from 2026-11-04 on Windows 11 x64 (.NET SDK 10.0.402, Intel Core 6th generation, 8 logical cores). Downloads and test data were on a USB hard disk (about 37 MB/s sequential).

## Tests

`dotnet test tests/IDMRea.Core.Tests -c Release`: 177 passed on `net10.0` and 3 on `net10.0-windows`, none failed. Engine tests also run on Linux in CI.

They cover, against a local HTTP server with switchable faults:

- Exact bytes for single and multi-part downloads, dynamic splitting, a server that ignores ranges, a busy server (fewer parts), a dropped part, a changed file, bad `Content-Range`, truncated bodies, and empty and tiny files.
- Pause, crash and restart: each part resumes from its saved offset, and saved progress never counts bytes that aren't on disk.
- A resumed download behind an expiring signed redirect gets a fresh link from its original URL.
- Not enough disk space fails before downloading; a resume needs space only for what is left.
- The finished file carries the internet zone mark (`Zone.Identifier`, ZoneId=3) without any user name or password from the link.
- Publishing never overwrites a file, and recovery after a crash matches files by SHA-256.
- Retries, `Retry-After`, the shared speed limit, the queue and the start-up rule.
- The pipe protocol (versions 1 and 2, `subscribe` pushes) and a server that takes 21 s to answer a link check.
- Settings, file names, categories, sidebar counts, search (Arabic-Indic digits search as 0-9) and the drawn icon.

The Recycle Bin test runs only with `IDMREA_TEST_RECYCLE=1`, because each run leaves its test file in the real Recycle Bin. CI sets it.

## The real app

`scripts/capture-windows.ps1` runs the published app and engine against `scripts/fixture-server.py` and drives the window through UI Automation. It passed. It checks:

- The window, sidebar, toolbar and search are present, in light and dark.
- A missing link is refused when added, with "no longer has this file".
- A link that disappears after the check ends in Failed; a connection cut halfway still completes with every byte; a large file runs in more than one part.
- Each list (Downloading, Queued, Completed, Failed, Documents) shows the right downloads.
- The Add dialog fills in name, size and type, and the download lands in the per-type folder.
- Settings reach the engine; under Ultra performance, parts per file shows 16 and can't be edited.
- Closing the window exits the app but leaves the engine downloading in the tray; reopening shows the same downloads.

Screenshots and `runtime-verification.json` are saved in `artifacts\screenshots`.

## CPU and memory

`scripts/measure-resources.ps1`, three 512 MB downloads from the local server at once:

| Phase | CPU (% of one core) | Memory at end |
|---|---|---|
| Engine idle | 0.08 % | 9.5 MB |
| 3 downloads, 13 MB/s | 10.88 % (3.2 % kernel) | 64.4 MB |
| Engine idle after | 0.24 % | 7.8 MB |
| App open and idle | 0.31 % | 161.5 MB |

The downloads cost 8.08 ms of CPU per MB, about 10 % of one core at a 100 Mbit/s line. That misses the 10 % target. Most of it (about 5.5 ms per MB) is the SHA-256 that every finished file gets before it is published, because this processor has no SHA instructions (181 MB/s). Newer processors hash several times faster.

Two problems found while measuring were fixed:

- **Far-off writes waited for NTFS.** Writing a part far into a pre-sized file made Windows zero-fill everything before it (9.6 s for a write at 448 MB on the USB disk). Part files are now sparse while downloading. Three 512 MB downloads went from 1.4 MB/s to 40 MB/s at the start.
- **Hashing read in 4 KB steps.** It now reads 1 MB at a time.

In the first 30 s after the window opens, .NET start-up alone uses about 15 % of one core; the app-idle phase starts after that.

## Not checked here

- The tray icon's look, its menu and notifications on screen.
- A live change of the Windows accent colour (changing it is a system setting).
- A signed installer and browser integration, which don't exist yet.
