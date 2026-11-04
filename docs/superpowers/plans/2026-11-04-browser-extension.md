# Browser Extension Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Chrome/Edge extension that finds real files on a page, stops click traps and bad sites, warns on bad files, and hands downloads to the IDM-Rea engine through a native host.

**Architecture:** MV3 extension in plain JS modules (`src/IDMRea.Extension`), a Windows native-messaging host (`src/IDMRea.BrowserHost`) relaying to the engine pipe, and engine/core additions (request headers, expected checksum, optional folder, browser settings).

**Tech Stack:** .NET 10, xunit; Chrome MV3 (service worker, declarativeNetRequest, webNavigation, downloads, cookies, nativeMessaging); `node --test`; Playwright 1.64 (global) Chromium for the end-to-end check.

**Spec:** `docs/superpowers/specs/2026-11-04-browser-extension-design.md`

## Global Constraints

- No em-dashes in any text the app or extension shows; no Arabic-Indic digits; no `type="number"` inputs.
- Commits as `Tem Revil <temrevil@gmail.com>`; 150+ changed lines end with `Co-Authored-By: Claude <noreply@anthropic.com>`.
- Never stop the owner's running IDM-Rea (data folder `artifacts\runtime-data`); tests use their own folders. Publishing over `artifacts\windows` needs the app closed: do it last, then relaunch the owner's copy.
- Probing: at most 1 KB per link (`Range: bytes=0-1023`), 4 at a time, 60 links per page, cached 10 minutes.
- Everything drawn in a page lives in a closed shadow root.
- A test is only trusted after it was seen failing.

## Review Focus

1. A normal site: real links with `target=_blank` to another site, and same-site new tabs, must still open.
2. A server that ignores `Range` on probe: the extension must not read the whole body (abort after headers + 1 KB).
3. A download whose link needs the page's cookies: takeover must pass them, or the engine gets an HTML login page.
4. Lists unreachable on first run: the extension still works (no blocking, no errors), and retries later.
5. IDM-Rea not installed or the host missing: browser downloads stay in the browser, the menu says IDM-Rea isn't connected.

---

### Task 1: Request headers, optional folder

**Files:** `src/IDMRea.Contracts/DownloadJob.cs`, `src/IDMRea.Contracts/Protocol.cs` (EngineRequest), `src/IDMRea.Core/RequestHeaders.cs` (new), `Prober.cs`, `MultiPartTransfer.cs`, `SingleStreamTransfer.cs`, `DownloadManager.cs`, `src/IDMRea.Engine/EngineHost.cs`, tests `DownloadTests.cs`, `IpcTests.cs`, Fixture gains `RequestHeaders` capture.

**Interfaces (produces):**
- `public sealed record RequestHeaders(string? Referrer = null, string? Cookies = null, string? UserAgent = null)` in Contracts; `DownloadJob.Headers` (`RequestHeaders?`, cookies stored as `ProtectedCookies` via `CookieVault.Protect/Unprotect` in Core: DPAPI CurrentUser on Windows, `null` elsewhere and kept in memory).
- `RequestHeaders.ApplyTo(HttpRequestMessage)`.
- `Prober.ProbeAsync(HttpClient, string url, CancellationToken, RequestHeaders? headers = null)`.
- `DownloadManager.AddAsync(string url, string? folder, string? fileName = null, bool startPaused = false, RequestHeaders? headers = null, string? expectedSha256 = null)`; `folder == null` -> `Settings.FolderFor(probe.Category)`.
- `EngineRequest` gains `Referrer, Cookies, UserAgent, ExpectedSha256`.

Tests (write first, see fail):
- `HeadersReachTheServerOnProbeAndEveryPart`: fixture records headers per request; after a multi-part download every request has the Referer, Cookie, User-Agent given.
- `CookiesAreNotStoredInPlainText` (Windows): `jobs2.db` bytes do not contain the cookie value; a new manager on the same folder still sends it.
- `AddWithoutFolderUsesTheTypeFolder`: settings folder for Documents set to a temp dir; adding `/report.pdf` lands there.
- `IpcAddCarriesHeaders`.

Commit: `feat: downloads carry the browser's referrer, cookies and user agent`.

### Task 2: Expected checksum

**Files:** `DownloadJob.cs` (`ExpectedSha256`, `Verified` bool), `ErrorKind.ChecksumMismatch`, `Text.cs`, `DownloadManager.cs` (after publish, hash with 1 MB reads only when expected is set), tests.

Tests: `MatchingChecksumIsMarkedVerified`, `WrongChecksumFailsWithAPlainReason` (status Failed, kind ChecksumMismatch, text "The file doesn't match the checksum on the site.", file kept), `ChecksumIsCaseInsensitive`.

Commit: `feat: verify a download against the checksum on the site`.

### Task 3: Browser settings

**Files:** `EngineSettings.cs` (`BrowserTakeover = true`, `BrowserMinBytes = 1048576`, clamped 0..1 GiB), `ContractsTests.cs`.

Test: `BrowserSettingsDefaultAndClamp`. Commit with Task 4.

### Task 4: Native host

**Files:** `src/IDMRea.BrowserHost/IDMRea.BrowserHost.csproj` (Exe, net10.0-windows10.0.19041.0, links `src/IDMRea.App/EngineClient.cs`), `Program.cs`, `NativeMessaging.cs` (framing), `HostHandler.cs` (hello/add/open), `src/IDMRea.Engine/Windows/BrowserRegistration.cs` (manifest + HKCU keys + target file; registry root injectable), `IDMRea.slnx`, `EngineClient.EnsureStartedAsync(string? enginePath = null)`.

**Interfaces:**
- Framing: `NativeMessaging.ReadAsync(Stream) -> JsonDocument?` (null at end), `WriteAsync(Stream, object)`; max 1 MB message.
- Messages in: `{type:"hello"}` -> `{ok, version, takeover, minBytes}`; `{type:"add", url, fileName?, referrer?, cookies?, userAgent?, sha256?}` -> `{ok, id?, error?}`; `{type:"open"}`.
- Data folder: `%LOCALAPPDATA%\IDMRea\browser-target.txt` if present, else default.
- `BrowserRegistration.Register(string hostExe, string dataFolder, RegistryKey root)`; extension id constant `BrowserRegistration.ExtensionId`.

Tests: `FramingRoundTrip`, `FramingRejectsOversize`, `HostAddsThroughTheEngine` (Host + real engine host on temp folder), `RegistrationWritesManifestAndKeys` (under `HKCU\Software\IDMRea.Tests\...`, deleted after).

Commit: `feat: native host connects the browser to the engine`.

### Task 5: Extension rules (pure)

**Files:** `src/IDMRea.Extension/lib/verdict.js`, `lib/links.js`, `lib/lists.js`, `lib/sites.js` (site of a host, file hosts), tests `src/IDMRea.Extension/test/*.test.js`, `src/IDMRea.Extension/package.json` (`"type":"module"`, `"test": "node --test test/"`).

**Interfaces:**
- `parseHosts(text) -> string[]`, `parseAbp(text) -> string[]` (only `||domain^` with no path), `DomainSet.has(host)` (matches parents).
- `siteOf(host)` (last two labels, three for known two-part suffixes such as `co.uk`, `com.sa`).
- `isCandidate({href, text, download}) -> boolean`; `expectedBytes(text) -> number|null`; `checksumNear(text) -> string|null`.
- `classify({url, pageUrl, status, contentType, disposition, bytes, firstBytes, linkText, nearText, lists}) -> {kind: 'file'|'page'|'host'|'ad'|'blocked'|'unknown', name, size, type, warnings: [{code, text}]}`.

Tests (each rule with a case that triggers it and one that doesn't): double extension, program disguised by first bytes (`MZ`), program over HTTP, small installer vs promised GB, malware/phishing list, ad link, file host page, HTML is a page, attachment HTML is a file, checksum found once / ambiguous, candidate words in Arabic and English.

Commit: `feat: extension rules for real files, bad files and bad sites`.

### Task 6: Extension runtime

**Files:** `manifest.json` (MV3, `key`, permissions: `nativeMessaging downloads cookies storage tabs webNavigation declarativeNetRequest scripting alarms`, `host_permissions: <all_urls>`), `background.js`, `content/guard-main.js`, `content/page.js`, `popup.html/css/js`, `blocked.html/js`, `icons/` (from the app's icon renderer, neutral grey ring), `_locales` not needed.

Behaviour as in the spec ("Trap rules in detail"). Lists: alarm daily, first fetch at install; dynamic rules chunked 5,000 domains per rule, redirect to `blocked.html?u=<url>&why=<list>`; per-tab "continue anyway" adds a session allow rule.

Verified by Task 8's end-to-end check.

Commit: `feat: browser extension (badges, menu, trap guard, takeover)`.

### Task 7: App

**Files:** `SettingsPage.xaml(.cs)`: Browser section (switch, smallest file text box `inputMode` numeric, "Add to browser" button -> engine command `registerBrowser` -> `BrowserRegistration.Register`, then opens the extension folder in Explorer and shows three steps); `DetailsPane`: "Checksum matches the site's" line; `EngineHost` command `registerBrowser`.

Test: `RegisterBrowserCommandWritesTarget` (IPC, test registry root via env `IDMREA_REGISTRY_ROOT`).

Commit: `feat: browser section in Settings`.

### Task 8: End-to-end check and publishing

**Files:** `scripts/fixture-server.py` (trap pages on two ports: `/trap.html` with an invisible full-page layer, a `window.open` popunder, a fake button to an ad host, a real `/game.iso` link with its SHA-256 beside it, a 2 MB `Setup.exe`; `/bad.html` listed as malware via a test list), `scripts/check-extension.mjs` (Playwright Chromium, headless, `--load-extension`, test lists served locally via `?lists=` override in storage, native host registered under the Chromium key for the run and removed after), `scripts/publish-windows.ps1` (publishes host into `engine\`, copies extension to `artifacts\windows\extension`), `.github/workflows/build.yml` (node tests on Linux).

Expected: all checks print `ok`, exit 0.

Commit: `test: end-to-end extension check against a trap page`.

### Task 9: Docs and vault

README (extension section, one line of credit kept), `docs/architecture.md`, `docs/verification.md`, vault Facts/History/Pending/Commit Index/Index.
