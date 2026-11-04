# Architecture

## Processes

- **App** (`IDMRea.App.exe`, WinUI 3, unpackaged, single instance): only shows and controls downloads.
  - A second launch hands its arguments to the running window and exits: `--job <id>` opens a download's details, `--add-clipboard` opens the Add dialog.
  - While the window is minimised it stops listening for updates.
- **Engine** (`engine\IDMRea.Engine.exe`): does all downloading.
  - The app starts it when needed. It keeps running after the window closes and shows the tray icon and notifications.
  - It holds `engine.lock` in the data folder, so a second engine exits with code 2.
- Closing the window either keeps downloading in the tray (default) or pauses everything and stops the engine (Settings).

## IPC (version 2)

One named pipe per data folder: `IDMRea.` + the first 20 hex characters of the SHA-256 of the folder path. It is open to the current user only. Each request is one line of JSON, and so is each reply. The engine allows 15 s to read a request; a link check (`probe`, and the one inside `add`) may take up to 30 s, so the app waits up to 45 s for those replies.

Commands:

- `list`, `probe`, `add`
- `pause`, `resume`, `cancel`, `retry`, `remove` (one id or several)
- `pauseAll`, `resumeAll`
- `getSettings`, `setSettings`
- `shutdown`
- `subscribe`, which keeps the line open. The engine first sends the full list, then only changes: changed jobs, removed ids, per-job speeds, totals and settings.

Version-1 clients still get version-1 replies.

## Downloads

1. **Probe.** A `GET` with `Range: bytes=0-0` gives the final URL, name (from `Content-Disposition` or the URL), size, type, category and whether the file can resume. Resuming needs ranges plus a strong ETag or Last-Modified.
2. **Name.** The name is made unique when the download is added: a clash gets a "(1)" name.
3. **Parts.** When the file can resume and its size is known, it is split into up to 8 parts (16 with Ultra), each at least 1 MiB (512 KiB with Ultra).
   - Extra parts start 250 ms apart (Ultra starts them all at once).
   - A free connection splits the part with the most left.
   - `429` or `503` on an extra part lowers the part count for that file.
   - The part file is sparse on Windows. Otherwise NTFS would zero-fill up to every far-off write, which takes seconds on slow disks. Since a sparse file takes no space up front, the free space is checked first: a download that can't fit fails with "Not enough disk space" before it starts.
   - A resumed download whose link redirected asks the original link again, because redirect targets often expire (signed links). With no answer it keeps the saved target.
4. **Checks on every response.** Status, `Content-Range`, total length and validator. A changed file stops with "The file changed on the server". A server that ignores ranges falls back to one connection.
5. **Checkpoints** every 500 ms and on stop. The part file is flushed to disk first, then each part's progress is saved, so saved progress never counts bytes that aren't on disk.
6. **Publishing.**
   - The finished part file is hashed (SHA-256) and the job saved as Finalizing.
   - The part file gets the internet zone mark (a `Zone.Identifier` stream with ZoneId=3 and the link, minus any user name or password), so Windows warns before running it.
   - The file is moved into place without overwriting anything there, then marked Completed.
   - After a crash, Finalizing jobs are matched by hash, and unrelated files are never touched.
7. **Retries.** Network errors, timeouts, `429` and `5xx` are retried up to the set number of times (default 5), after 2, 5, 15, 30 and 60 s, or after the server's `Retry-After` (at most 10 minutes). Other errors fail at once with a plain-language reason.
8. **Speed.** One limit is shared by all downloads and changes apply at once. The speed shown is the average over the last 3 s.

## Start-up rule

When the engine starts, downloads that were Downloading, Queued, Retrying, or paused by the engine itself (for example at shutdown) carry on by themselves. Downloads paused by the person stay paused.

## Data

Stored in `%LOCALAPPDATA%\IDMRea`; `IDMREA_DATA_DIRECTORY` overrides it (used by the test scripts). App, engine and scripts must agree on the folder.

| File | What |
|---|---|
| `jobs2.db` | SQLite (WAL, synchronous FULL): one JSON row per job, plus settings. A version-1 `jobs.db` is copied in once. |
| `engine.lock` | held by the running engine |
| `app-theme.txt` | the theme, read before the first frame so the window doesn't flash |
| `icons\ring-RRGGBB.ico/png` | the icon drawn in each accent colour used |
| `app-startup.log` | app start-up errors |

The database contains download URLs; never commit it. Nothing here protects against another process running as the same user.

## Look

- Windows 11 controls: Mica, the title bar control, navigation view and content dialogs.
- Follows Windows light or dark mode (or the theme set in Settings) and the Windows accent colour, including live changes.
- The icon, a progress ring in three parts around a chevron on a charcoal tile, is drawn at run time (`IconArt`) with the accent as the ring. A lighter shade is used if the accent would disappear on the tile.
- The exe's own icon file, which Windows shows before the app runs, keeps a fixed copper ring. `scripts/make-icon.cs` writes it from the same code.
