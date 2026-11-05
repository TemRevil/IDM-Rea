# Architecture

## Processes

- **App** (`IDMRea.App.exe`, WinUI 3, unpackaged, one window per data folder): only shows and controls downloads.
  - A second launch hands its arguments to the running window and exits: `--job <id>` opens a download's details, `--add-clipboard` opens the Add dialog, `--settings` opens Settings.
  - While the window is minimised it stops listening for updates.
- **Engine** (`engine\IDMRea.Engine.exe`): does all downloading.
  - The app starts it when needed. It keeps running after the window closes and shows the tray icon and notifications.
  - It holds `engine.lock` in the data folder, so a second engine exits with code 2.
- **Native host** (`engine\IDMRea.BrowserHost.exe`): started by the browser for the extension. It speaks the browser's native messaging (4-byte length, then JSON) and relays to the engine pipe, starting the engine if needed. Messages: `hello` (version, take-over settings), `add` (link, name, referrer, cookies, user agent, SHA-256), `probeVideo` and `addVideo` (a page's video, see Videos), `open` (shows the app).
- **Extension** (`extension\`, Manifest V3, plain JavaScript, no build step). See below.
- Closing the window either keeps downloading in the tray (default) or pauses everything and stops the engine (Settings).
- The tray icon's right-click menu is built fresh each time it opens and follows Windows' light or dark mode. It shows how many files are downloading and how fast, then: Open IDM-Rea (also a double-click), Add from clipboard, Pause all and Resume all (each enabled only when it applies), a Speed limit submenu (No limit, 512 KB/s to 10 MB/s; a limit typed in Settings stays listed and ticked), Open downloads folder, Settings, and Quit.

## IPC (version 2)

One named pipe per data folder: `IDMRea.` + the first 20 hex characters of the SHA-256 of the folder path. It is open to the current user only. Each request is one line of JSON, and so is each reply. The engine allows 15 s to read a request; a link check (`probe`, and the one inside `add`) may take up to 30 s, so the app waits up to 45 s for those replies. Asking yt-dlp (`probeVideo`, `addVideo`, `updateVideoTools`) may take up to 90 s, and the app waits 105 s.

Commands:

- `list`, `probe`, `add`
- `pause`, `resume`, `cancel`, `retry`, `remove` (one id or several)
- `pauseAll`, `resumeAll`
- `getSettings`, `setSettings`
- `registerBrowser` ("Add to browser")
- `probeVideo` (a page's title and qualities, or the tools' setup progress while they are being fetched), `addVideo` (a page and a quality), `videoTools` (their state), `updateVideoTools` (Settings' Set up now / Update now)
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
   - The finished part file's Windows file ID (volume serial and file index, which stay with the file through the move) is recorded and the job saved as Finalizing.
   - The part file gets the internet zone mark (a `Zone.Identifier` stream with ZoneId=3 and the link, minus any user name or password), so Windows warns before running it.
   - The file is moved into place without overwriting anything there, then marked Completed.
   - After a crash, Finalizing jobs are matched by size and file ID (size only on other systems), and unrelated files are never touched. If another file took the name, the job fails with "Another file now has this name"; if the finished file vanished, it fails and Retry downloads it again.
7. **Retries.** Network errors, timeouts, `429` and `5xx` are retried up to the set number of times (default 5), after 2, 5, 15, 30 and 60 s, or after the server's `Retry-After` (at most 10 minutes). Other errors fail at once with a plain-language reason.
8. **Speed.** One limit is shared by all downloads and changes apply at once. The speed shown is the average over the last 3 s.

## Videos

1. **Tools.** yt-dlp, Deno and FFmpeg live in `tools\` in the data folder. The first video question starts setting them up in the background and answers with the progress; Settings can start it too. Each file comes from the project's latest GitHub release and is checked against the release's SHA-256 list before use; archives are unpacked flat, so no path inside them is trusted. yt-dlp is looked at for a newer release once a day (Update now looks at once). A newer yt-dlp waits beside the old one while any video job uses it, and takes its place when none does.
2. **Finding the video.** `yt-dlp -J` on the page, with the browser's cookies (for the page's own host only, written to a cookies file only for that run, then deleted; a media address on another host gets none), referrer and user agent. For each height IDM-Rea offers one choice: the best picture (AVC first, then AV1, then VP9) with matching sound (AAC for MP4, Opus for WebM), or one stream holding both, plus "Sound only". A stream yt-dlp can't describe (a bare HLS list) is offered as one "Video" choice. A page yt-dlp doesn't know is tried through the media addresses the browser saw it load, first with yt-dlp, then as plain files.
3. **Downloading.** The job keeps its page, its choice and one entry per stream. Streams are fetched one after another into their own part files:
   - Direct streams go through the same multi-part transfer as files, never asking for more than the stream's chunk size per request (YouTube slows larger ones).
   - HLS lists are fetched segment by segment, a few ahead, appended in order and checkpointed per segment; AES-128 segments are decrypted with the list's key and IV (or the sequence number). Byte-range segments and an init segment are honoured. Live lists, SAMPLE-AES and other key formats (DRM) are refused.
   - Stream addresses expire. A 403 or 410 asks yt-dlp again, once per run, for the same choice; a stream whose size changed starts over.
4. **Joining.** FFmpeg copies the streams into one MP4, WebM or MKV (`-c copy`, no re-encoding). A pause or crash during the join joins again on resume. The stream part files are deleted once the file is in place, and when the job is cancelled or removed.

## Browser extension

- `background.js` (service worker): checks links with a `Range: bytes=0-1023` request (4 at a time, cached 10 minutes), takes over browser downloads, guards tabs, and keeps the lists.
  - **Link checks** carry the browser's cookies only for links on the page's own site, and never reach this PC or the local network (`localhost`, `10.x`, `192.168.x`, link-local and the like) unless the page itself is on that host. A call to IDM-Rea that can't reach it at all is tried once more after 1.5 s, since the host is briefly missing while IDM-Rea is updated.
  - **Take over:** the browser's download is paused, sent to IDM-Rea with the page's cookies, referrer and user agent, and erased only once IDM-Rea accepts it; otherwise it resumes in the browser. Files smaller than the Settings floor (1 MB) and private windows stay in the browser.
  - **New tabs:** a tab opened by a page to another site is closed unless the page reported, in the last 1.5 s, a click on a link to that site or on a button. Ad networks are always closed. The page shows "Blocked a pop-up to example.com" with "Open it".
  - **Redirects:** a tab sent to an ad server without being typed or clicked as a link to it goes back.
  - **Lists**, downloaded at install and daily, matched with parent domains: URLhaus and the phishing filter (malware-filter), and EasyList's pop-up and ad servers. Malware, phishing and pop-up sites become blocking rules for pages and frames, which open `blocked.html` with the reason; "Continue anyway" allows the site in that tab until it closes.
- `content/guard-main.js` (in the page's own world, before its scripts): `window.open` to another site works only during a click on a button or on a link to that site.
- `content/page.js`: reports clicks; takes away click-catching layers (a fixed or absolute element over at least half the window that is invisible, or a link or frame to an ad server, is stopped at the press; an empty see-through one is removed only if the press opens a pop-up, since menus use those to close); finds candidate links and draws badges, the card and notices in a closed shadow root on a fixed layer.
- **Videos:** the service worker notes each tab's media requests (HLS and DASH lists, video files; not segments, short sounds or `blob:`). The page shows "Download video" at the top right of a video at least 200x112 that is visible and is not a silent loop without controls; the card then lists the qualities, or the tools' setup progress the first time. The toolbar menu asks for the page's video when the page has a video, frames or noted media.
- `lib/`: the rules as pure functions (`verdict.js`, `links.js`, `lists.js`, `sites.js`, `guard.js`, `video.js`), tested with `node --test`.
- A switch in the menu turns the click-trap guard off for one site. Nothing about the pages visited leaves the PC; only the lists are downloaded.
- The extension's ID is fixed by the `key` in its manifest, and the host accepts only that ID.

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
| `tools\` | yt-dlp, Deno and FFmpeg, with yt-dlp's version and when it was last checked |
| `temp\` | yt-dlp's cookies file, only while it runs |
| `browser\com.idmrea.host.json` | the native host manifest, written by "Add to browser" (rewritten only when it changes, never half-written) |
| `browser\extension\` | the copy of the extension the browser loads unpacked; "Add to browser" makes it and each engine start refreshes it from the app, so it outlives an app update or move |

"Add to browser" also points `HKCU\Software\{Google\Chrome, Microsoft\Edge, Chromium}\NativeMessagingHosts\com.idmrea.host` at that manifest, and writes `%LOCALAPPDATA%\IDMRea\browser-target.txt` with the data folder the host should use (the browser starts it without the app's environment; an `IDMREA_DATA_DIRECTORY` in the browser's own environment wins). Cookies sent with a browser download are stored with the job encrypted for the current Windows user (DPAPI).

The database contains download URLs; never commit it. Nothing here protects against another process running as the same user.

## Look

- Windows 11 controls: Mica, the title bar control, navigation view and content dialogs.
- Follows Windows light or dark mode (or the theme set in Settings) and the Windows accent colour, including live changes.
- The icon, a progress ring in three parts around a chevron on a charcoal tile, is drawn at run time (`IconArt`) with the accent as the ring. A lighter shade is used if the accent would disappear on the tile.
- The exe's own icon file, which Windows shows before the app runs, keeps a fixed neutral grey ring. `scripts/make-icon.cs` writes it from the same code.
