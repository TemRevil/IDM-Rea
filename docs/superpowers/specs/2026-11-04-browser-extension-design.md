# Browser extension - design

## Why

People download from sites that fight them: game and file sites where a click on empty space or on a "Download" button opens a new tab or redirects to another "Download" page that leads to a scam, a fake downloader or malware. The extension makes downloading from such sites safe and fast: it shows which links are real files, stops the traps, and hands real downloads to the IDM-Rea engine (multi-part, resumable).

Success: on a hostile download site the person sees a badge on every real file with its name and size, a click on empty space or a fake button does nothing harmful, and the real file downloads through IDM-Rea at full speed.

## Goals

1. **Find real files.** Every candidate link on the page is checked against the server before anyone clicks, from the browser itself (so the page's cookies apply): status, type, name, size, and the first bytes of the file.
2. **Show them.** A small badge next to each real file (name, size, type). Hovering it shows a card with details, warnings and "Download with IDM-Rea". A toolbar menu lists every real file found on the page, grouped by type, with checkboxes and "Download selected".
3. **Stop click traps.** Remove invisible layers that catch clicks; block `window.open` calls and new tabs that a page opens to another site when the click was not on a link to that address; block navigation to pop-up and ad networks.
4. **Know bad sites.** Pages and file links on malware or phishing lists are blocked with a plain page saying why, with Go back and Continue anyway.
5. **Know bad files.** Warn, with a reason in plain words, on:
   - a double extension that ends in a program (`invoice.pdf.exe`);
   - content that is a program while the name or the server's type says otherwise (first bytes `MZ`, ELF and similar);
   - a program served over plain HTTP;
   - a small installer or "downloader" where the page promises a large file (a link text or page that names a size in GB, while the file is a program under 50 MB);
   - a file whose site is on a bad list.
6. **Check the file after download.** When the page shows a SHA-256 next to the link, it travels with the download and the engine compares it when the file is finished: "Checksum matches the site's" or Failed "The file doesn't match the checksum on the site".
7. **Take over browser downloads** (switch in Settings, on by default): a download the browser starts is cancelled in the browser and handed to IDM-Rea with its cookies, referrer and user agent. Files smaller than a set size (default 1 MB), `blob:` and `data:` links, and anything when IDM-Rea isn't reachable stay with the browser.

## Non-goals

- Not a general ad blocker: ads that don't trap clicks are left alone.
- No AI or cloud verdicts. All checks are local rules plus public lists.
- Firefox: later (not installed here, so it can't be verified now).
- Store publishing: needs the owner's developer accounts; until then the extension is loaded from the app folder.

## Anti-goals

- Breaking normal sites: a click on a real link must always work, and an opened tab to the same site is never touched.
- Sending browsing history anywhere: lists are downloaded and checked locally; no page address leaves the PC.
- Crying wolf: a warning always states its reason; a file with no reason gets no warning.

## Parts

### Extension (`src/IDMRea.Extension`, Manifest V3, plain JavaScript modules, no build step)

- `background.js` (service worker): native messaging to IDM-Rea, the probe queue (at most 4 at a time, cached per URL for 10 minutes, `Range: bytes=0-1023` with the page's cookies), download takeover, the new-tab guard, list updates, the toolbar badge count.
- `content/guard-main.js` (page world, `document_start`): wraps `window.open`; a call to another site is allowed only during a click on a link or button whose address is that site. Blocked calls are reported to the content script.
- `content/page.js` (isolated world): finds candidate links, asks for probes, draws badges and the hover card in a closed shadow root (the page's CSS can't reach them), removes click-catching layers (a fixed or absolute element covering most of the window that is invisible or nearly invisible, or a link to an ad server stacked over the page), and tracks the last real link click for the new-tab guard.
- `popup.html` / `popup.js`: the toolbar menu.
- `blocked.html` / `blocked.js`: the page shown instead of a blocked site.
- `lib/verdict.js`, `lib/links.js`, `lib/lists.js`: pure functions (rules, link candidates, hash finding, list parsing), tested with `node --test`.
- Lists: downloaded on first run and then daily, cached in extension storage, matched by domain and parent domains:
  - malware: `https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-hosts.txt`
  - phishing: `https://malware-filter.gitlab.io/malware-filter/phishing-filter-hosts.txt`
  - pop-up networks: `https://raw.githubusercontent.com/easylist/easylist/master/easylist/easylist_adservers_popup.txt`
  - ad servers: `https://raw.githubusercontent.com/easylist/easylist/master/easylist/easylist_adservers.txt`
  Malware, phishing and pop-up domains become dynamic blocking rules for top-level and frame navigation (redirected to `blocked.html`). Ad servers are used to judge links and opened tabs, and block top-level navigation only when it was not typed or clicked as a plain link by the person.
- A per-site switch in the menu turns the guard off for that site.
- Fixed extension ID via the manifest `key`, so the native host can trust it.

### Native host (`src/IDMRea.BrowserHost`, Windows console exe)

- Speaks the browser's native messaging framing (4-byte length, JSON) on stdin/stdout and relays to the engine pipe, starting the engine if it isn't running (same as the app does).
- Messages: `hello` (versions, takeover settings), `add` (url, file name, referrer, cookies, user agent, expected SHA-256, folder by category), `open` (shows the app).
- Published into the engine folder (`engine\IDMRea.BrowserHost.exe`), so it starts `IDMRea.Engine.exe` beside it.
- Registered only when the person presses "Add to browser" in Settings: for Chrome and Edge under the current user (`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.idmrea.host`, the same under `Microsoft\Edge`), manifest in `<data folder>\browser\com.idmrea.host.json`. The browser starts the host without the app's environment, so the same press writes `%LOCALAPPDATA%\IDMRea\browser-target.txt` with the data folder to use; with no such file the host uses the default data folder. Test runs never press it, so they never take over the browser link. At start the engine refreshes the manifest's exe path only when the target file names its own data folder.

### Engine and core

- Jobs carry request headers (Referer, Cookie, User-Agent) used by the probe and every transfer request. Cookies are stored encrypted for the current Windows user (DPAPI); on other systems they are kept in memory only.
- Jobs carry an optional expected SHA-256; after the move into place the engine hashes the file once and records Verified or fails with the mismatch reason.
- Settings: `BrowserTakeover` (default on), `BrowserMinBytes` (default 1 MB).
- IPC `add` gains `referrer`, `cookies`, `userAgent`, `expectedSha256`; `folder` becomes optional, and without it the engine uses the per-type folder for the probed category.

### Trap rules in detail

- **Click-catching layer:** on `pointerdown` (capture phase), the topmost element at the point (or its positioned ancestor) is fixed or absolute, covers at least half the window, and is invisible (opacity at most 0.1) or empty (transparent background, no visible text, image, video or canvas), or is a link or frame to an ad server. The event is stopped, the layer gets `pointer-events: none`, and the element underneath is clicked if it is a link or button.
- **New tab guard:** when a page opens a tab (`webNavigation.onCreatedNavigationTarget`) to another site, and the page did not report a click on a link to that address in the last 1.5 s, the tab is closed and the page shows a quiet notice "Blocked a pop-up to example.com" with "Open it".
- **Redirect guard:** a top-level navigation to an ad-server domain that the person did not click as a link in the last 1.5 s is sent back (`history.back` in the tab, or `blocked.html` when there is no history).
- **Candidate links:** `http(s)` links that have a `download` attribute, a file extension from the known list (archives, disc images, programs, video, audio, documents, split parts `.001`), point to a known file host, or whose text says download (English, Arabic, and the common European words). Nearest to the visible area first, at most 60.
- **File or page:** a 2xx answer whose type is not `text/html` (or which says `attachment`) is a file; HTML is a page. Known file host pages get a quiet "File host" badge. Ad-server links get a red "Ad link, not a download" badge.
- **Checksum near a link:** one 64-character hex string within the link's nearest block (300 characters around it); several different ones means none is used.

### Tests

- `node --test` for the pure rules, link candidates, checksum finding and list parsing.
- C# tests for the host framing and relay, request headers reaching the server, DPAPI round trip, checksum verification, optional folder, registration files.
- An end-to-end check with Playwright's Chromium (headless, extension loaded) against a trap page from the fixture server: badges on real files, the invisible layer removed, `window.open` to another site blocked, a fake button's tab closed, a malware-listed page blocked, and a browser download handed to the engine.

### App

- Settings gets a "Browser" section: take over browser downloads, smallest file to take over, and "Add to browser" which opens the extension folder and the browser's extensions page with three plain steps (until the stores list it).
- The details pane shows "Checksum matches the site's" when verified.

## Constraints

- Owner rules: no em-dashes in any shown text; no Arabic-Indic digits; calm, restrained look matching the app (accent follows Windows in the app; the extension uses a neutral charcoal and the app's ring icon).
- Everything visible in the page is inside a closed shadow root and never shifts the page's layout.
- Probing never downloads more than 1 KB per link and stops at 60 links per page.

## Calls made by the builder (not discussed with the owner)

- Chromium browsers first; Firefox later.
- Free public lists only; a Google Safe Browsing key is not used.
- Takeover on by default with a 1 MB floor.
- Cookies kept with the job, encrypted with DPAPI.
- The 50 MB "small installer" threshold and the 60-link cap.
