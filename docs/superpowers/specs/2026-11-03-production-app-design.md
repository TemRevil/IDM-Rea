# IDM-Rea production app - design

Date: 2026-11-03. Status: draft for owner approval.

## Intent

IDM-Rea works (resumable HTTP, a background engine, a WinUI window) but looks and behaves like a
prototype: one page with a marketing headline, three buttons per row, no settings, one connection
per file, no tray, no notifications. The goal is a download manager that feels finished on
Windows 11: organised by sections, fast (multi-part downloads), quiet on the PC, and every control
on screen really does something.

Success: a person can install nothing, open `IDMRea.App.exe`, paste a link, see the name and size
before starting, watch it download in parts at full speed, close the window, get a Windows
notification when it is done, and find the file sorted into the right folder. Idle, the app costs
almost nothing.

## Goals

1. Sections: sidebar with status filters and file-type categories, search, a details pane,
   a Settings page, an Add download dialog.
2. Multi-part downloads with dynamic splitting, safe resume of every part.
3. Auto-retry on dropped connections, live speed and time left.
4. Tray icon and Windows notifications; optional start with Windows.
5. Low resource use by default; one Ultra performance switch.
6. Windows 11 native look: Mica, Fluent controls and icons, accent `#C46532`, light/dark/system.

## Non-goals

Browser integration or extension, installer, code signing, Microsoft Store, scheduler, video
grabbing, FTP/BitTorrent, proxy settings, per-download speed limits, accounts or sync, Arabic or
right-to-left UI (English only for now).

## Anti-goals (a failure even if it "works")

- A setting, button or menu item that changes nothing, or a stub labelled "coming soon".
- Weakening the current resume safety (validator checks, Content-Range checks, hash-checked
  publication, never overwriting an existing file).
- Losing or corrupting jobs already saved in a version-1 database.
- The UI process staying alive in the background after the window closes.
- A flashy look: no gradients, glow, bouncing or fast motion. Calm, plain, Windows-native.
- Marketing copy inside the app ("Your downloads, in motion." goes).

## Constraints

- C#/.NET 10, WinUI 3 on the Windows App SDK (update to the current stable release if newer than
  2.6.5), unpackaged, self-contained, x64. Keep the existing project split
  (App / Engine / Core / Contracts) and the existing tests passing.
- `IDMRea.Core` stays cross-platform (its tests run on Linux in CI). Windows-only code (tray,
  notifications, registry) lives in the Engine or App.
- No em-dashes in any text the app shows; use " - " or a comma.
- Digits are always 0-9 (format numbers with the invariant culture; convert any Arabic-Indic
  digits typed into a field).
- Every new behaviour gets a test that was seen failing first.
- Repo files carry no personal details. Commits authored Tem Revil <temrevil@gmail.com>.

## How it works (decided)

### Processes

- **Engine** (`IDMRea.Engine.exe`) owns everything long-lived: downloads, settings, the tray icon
  and notifications. It becomes a windowless Windows app (no console) with a hidden message
  window for the tray. It starts when the app opens, when Windows starts (if enabled), or from
  the tray.
- **App** (`IDMRea.App.exe`) is only the window. Closing it ends the process; the engine keeps
  running in the tray (or pauses everything and quits, per setting). A second launch brings the
  existing window forward instead of opening another.
- Tray "Open" and a notification click start the app (or focus it) and can open a given job's
  details.
- **On engine start** (reboot, crash, start with Windows): downloads that were running or queued
  carry on by themselves (owner's decision); ones the person paused stay paused; a saved retry
  wait continues from its stored time. Tray Quit and "pause all and quit" mark jobs as paused by
  the person, so nothing restarts until they resume. Windows sign-out or shutdown is not a Quit:
  jobs continue at next start. This replaces today's "interrupted jobs stay paused"; update
  `docs/architecture.md`.
- The engine project builds for both Linux and Windows; tray, notifications, registry and the
  windowless entry point compile only for Windows. The real engine-process test keeps running on
  Linux and gains Windows-only cases for the Windows parts. *(Designer's call.)*

### IPC (protocol version 2)

- Same per-user named pipe, current user only. Version 1 requests are still answered.
- Request/response commands: `list`, `probe`, `add`, `pause`, `resume`, `cancel`, `retry`,
  `remove` (with or without deleting the file), `pauseAll`, `resumeAll`, `getSettings`,
  `setSettings`, `shutdown`.
- A long-lived `subscribe` connection: the engine pushes only changed jobs plus totals (overall
  speed, active count), at most 4 times a second while something changes, nothing while idle.
  This replaces the app's 750 ms polling. The app unsubscribes while minimised.

### Data

- `jobs` table keeps one JSON payload per job; schema version 2 adds fields: file name, category,
  created/completed time, attempts, next retry time, last error kind, and the list of parts
  (start, end, received). Version-1 rows load as single-part jobs. Speed and time left are not
  stored; the engine computes them.
- New `settings` table (key/value JSON).
- The new engine uses a new database file (`jobs2.db`). On first start it copies every row of
  `jobs.db` into it as single-part jobs and never writes `jobs.db` again, so an older build
  can still open its own data safely. The engine also refuses to open a database whose schema
  version is newer than it knows. *(Designer's call.)*
- Paused jobs record who paused them (the person, or the engine on shutdown) for the start rule
  below.

### Multi-part transfer

- **Probe first** (on Add, and before a job starts): `GET` with `Range: bytes=0-0`. Learn total
  size, whether ranges work, ETag / Last-Modified, file name (`Content-Disposition`, including
  RFC 5987 `filename*`), and content type. Falls back to a plain `GET` if the server refuses.
- **Parts** only when the server answers 206 with a total size and a validator (strong ETag
  preferred, Last-Modified fallback). Otherwise one stream, exactly as today.
- The `.part` file is sized to the total up front; each part writes at its own offset. Parts
  start at `min(parts setting, total / 1 MiB)`.
- **Dynamic splitting:** when a part finishes and another has more than 2 MiB left, split that
  one in half and give the second half to a new connection, until the parts limit.
- Every part's response must match the probe's total and validator; any mismatch stops the job
  with "The file changed on the server" and Retry restarts it from zero.
- Each part's received count is saved only after its bytes are flushed (same rule as today), so
  resume re-requests exactly what is missing. Completion: all parts done, hash, Finalizing,
  move without overwrite, Completed - unchanged.
- Servers limiting connections (429/503 on extra parts): drop back to fewer parts for that job.

### Speed limit, retry, speed

- **Speed limit:** one token bucket shared by all connections; off by default.
- **Auto-retry:** network errors, timeouts, 408, 429 (honour `Retry-After`), 5xx retry after
  2, 5, 15, 30, 60 s, up to the retry setting (default 5), then Failed. A single part's dropped
  connection reconnects on its own while the others keep going and does not count. The limit
  counts whole-job failures in a row with no new bytes in between: any progress resets it, so a
  large file on a flaky line keeps going as long as it moves. *(Designer's call.)* No retry on other 4xx,
  file-changed, disk full or access denied. When Windows reports the network is back, waiting
  jobs retry at once. Status shows "Retrying in 12 s (attempt 2 of 5)".
- **Speed and time left:** per job, a moving average over about 3 s; totals in the status bar,
  tray tooltip and sidebar.

### Categories

Video, Music, Documents, Programs, Archives, Images, Other, picked by file extension (and content
type when there is no extension). Each has a save folder; defaults are `Downloads\<Category>`,
Other is `Downloads`. Changing a category's folder affects new downloads only.

### Resource use

- Default (Balanced): parts per file 8, downloads at once 3, 256 KiB pooled buffers, UI updates
  4 times a second at most, none while minimised.
- **Ultra performance** switch: maximum download speed while the app itself stays light.
  While it is on, parts per file is fixed at 16 (the Settings field shows 16, disabled, with
  "Set by Ultra performance"), buffers are 1 MiB, minimum part size is 512 KiB, and the first
  parts start at once instead of ramping up. Downloads at once stays the person's own setting.
  Turning Ultra off restores their parts value. The resource guards below stay on in both
  modes. *(Designer's call.)*
- Always: buffers from a pool (no per-read allocation), workstation non-concurrent GC in the
  engine, memory trimmed after downloads go idle, no timers while idle, the UI process exits
  when the window closes.
- Targets, measured by a script on this PC with the local test server:
  - engine idle: under 1% CPU, under 40 MB working set;
  - engine with 3 downloads at full speed on localhost: under 10% of one core (Balanced);
  - window open, nothing downloading: under 1% CPU.
  The builder reports the real numbers. If a target can't be met, say so with the measurement;
  don't change the target quietly.

### Tray and notifications

- Tray icon (engine). Tooltip: "2 downloading - 8.1 MB/s" or "IDM-Rea - idle". Menu: Open
  IDM-Rea, Add from clipboard, Pause all, Resume all, Quit (pause all and stop). Double-click
  opens the window.
- Notifications (Windows toast from the tray icon) when a download finishes ("Open" / "Show in
  folder") or fails ("Details"). Each can be turned off in Settings.
- Start with Windows: a per-user `Run` registry entry that starts the engine in the tray. Off by
  default.

### Screens

Window: Mica backdrop, custom title bar with app icon, name and a search box. Left
`NavigationView`:

- **Downloads**: All, Downloading, Queued and paused, Completed, Failed, each with a count.
- **Categories**: the seven above, each with a count.
- Footer: Settings.

Main area: toolbar (Add download, Resume all, Pause all, sort: newest / name / size / status) and
the list. A row: file-type icon, name, one status line ("12.4 MB of 640 MB - 8.2 MB/s - 1 min
left", "Retrying in 12 s", "Done - 640 MB", "Failed - server refused the request"), a thin
progress bar, and on hover/focus Pause or Resume, Show in folder, and a "More" menu. Multi-select
with bulk Pause / Resume / Remove. Each section has its own empty state with one plain line and,
where it makes sense, the Add button.

**Details pane** (right side, opens on selection, can be closed): name, status, full URL (copy),
saved to (open folder), size, downloaded, speed, time left, parts map (a bar showing each part's
progress), resumable yes/no, added and finished time, attempts, the error with a plain fix
("The server no longer has this file. Check the link."). Actions: Open, Show in folder, Copy link,
Pause/Resume, Retry, Remove from list, Delete file (asks first; the file goes to the Recycle
Bin, and the dialog says it can be restored from there; a leftover `.part` file is deleted
outright). The Recycle Bin call lives in the Windows-only part of the engine. *(Designer's
call.)*

**Add download dialog** (Add button, Ctrl+N, Ctrl+V on the window with a link in the clipboard,
dropping a link on the window, tray "Add from clipboard"): URL prefilled from the clipboard when it
holds a link; probes at once and shows name (editable), size, type and category, "Can resume"
yes/no, folder (category default, Browse). Warns if the same URL is already in the list (Open
existing / Download again) and shows the "(1)" name if the file exists. Buttons: Start
download, Download later (added paused), Cancel.

**Settings** page, sections:
- General: when I close the window (keep downloading in the tray / pause all and quit), start
  with Windows, notifications on finish / on failure.
- Downloads: default folder per category, downloads at once (1-10), parts per file (1-16),
  speed limit (off / KB/s / MB/s), auto-retry attempts (0-10).
- Performance: Ultra performance switch with one line on what it does.
- Appearance: theme (System / Light / Dark).
- About: version, engine status, data folder (open), stop engine.

Settings save as they change and the engine applies them to running work where it can (speed
limit and downloads at once at once; parts per file to the next split).

Keyboard: Ctrl+N add, Ctrl+V add from clipboard, Ctrl+F search, Space pause/resume, Enter open
file, Delete remove, Ctrl+A select all. All controls have accessibility names (the UI test
depends on them).

### Look

Windows 11 Fluent: Segoe UI Variable, Segoe Fluent Icons, Mica (solid background on Windows 10),
accent `#C46532` set as the app's accent colour in both themes, default Fluent motion only
(no custom animations beyond calm fades). An app icon in the same accent for window, exe, tray
and notifications.

### Verification

- Core tests (Linux and Windows): multi-part exact bytes, resume of a multi-part job after a kill,
  dynamic split, fallback when ranges are refused, file changed mid-way, speed limit within 10%,
  retry and backoff with a server that drops connections, 429 with Retry-After, settings save and
  load, category mapping, probe file names, v1 database migration.
- `scripts/fixture-server.py` gains range-refusing, connection-dropping and slow modes.
- `scripts/capture-windows.ps1` covers the new screens (each section, details pane, Add dialog,
  Settings) and works on Windows PowerShell 5.1 too.
- A resource script measures the targets above and prints the numbers.
- The owner tests the built app on this PC before anything is pushed.

### Delivery

Branch `production-app` off `Origins`, local commits, README and docs updated (README keeps the
"Built by Tem Revil, with Claude as coding assistant" line). Pushing or a PR only on the owner's
word.

## Left to the builder

Exact spacing and sizes, icon glyphs, message field names, thresholds within the stated bounds,
GC and publish settings, tray implementation (raw Win32 or a small library), folder picker API,
how the parts map is drawn, test structure.
