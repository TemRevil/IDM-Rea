# IDM-Rea

A Windows download manager written in C# with WinUI 3. It contains no IDM code or proprietary binaries.

## What it does

- **Downloads in parts.** Servers that allow byte ranges get up to 8 parts per file (16 with Ultra performance), and a part that finishes early splits the slowest one so every connection stays busy. Downloads resume after a pause, a crash or a restart, but only when the server proves the file hasn't changed (strong ETag, or Last-Modified).
- **Retries on its own.** Network errors, timeouts and busy servers are retried up to 5 times, after 2, 5, 15, 30 and 60 s. A server's `Retry-After` is honoured, up to 10 minutes.
- **Keeps working without the window.** A small background engine does the downloading. Closing the window leaves it running in the notification area, and unfinished downloads carry on after a restart. Notifications appear when a download finishes or fails.
- **Files by type.** Video, Music, Documents, Programs, Archives, Images and Other each have their own folder.
- **The app:**
  - Lists: All, Downloading, Queued and paused, Completed, Failed, plus one per type.
  - Search and sort, actions on several downloads at once, and a details pane that shows each part.
  - An Add dialog that checks the link first and shows its name, size, type and whether it can resume. It picks up a link from the clipboard (Ctrl+N or Ctrl+V) or from a link dropped on the window.
- **Browser extension** for Chrome, Edge and other Chromium browsers:
  - Marks the real files among a page's links (a 1 KB check of each, at most 60 per page) with their type and size, and a card with "Download with IDM-Rea". The toolbar menu lists them by type to download several at once.
  - Says what is wrong with a file: a program named like a document, a 2 MB installer offered as a 4 GB game, a downloader instead of the file, a program sent without encryption.
  - Stops click traps: invisible layers over the page, pop-ups and new tabs opened by a click on empty space or a fake button, and redirects to ad servers.
  - Blocks sites on public malware, phishing and pop-up ad lists, with the reason and a way through.
  - Hands browser downloads to IDM-Rea with the site's sign-in, and checks the file against a SHA-256 written next to its link.
- **Settings:** downloads at once, parts per file, a shared speed limit, retries, what closing the window does, start with Windows, notifications, theme, per-type folders, and the browser link.
- **Follows Windows.** Light or dark mode and the Windows accent colour. The icon is drawn in the accent colour too.

## Build and run

Needs Windows x64 and the .NET 10 SDK.

```powershell
pwsh scripts/publish-windows.ps1          # tests, then publishes the app, engine, browser host and extension to artifacts\windows
artifacts\windows\IDMRea.App.exe          # keep the folder together: the engine lives in engine\
```

Engine tests also run on Linux: `dotnet test tests/IDMRea.Core.Tests -c Release`. The extension's rules: `npm test` in `src/IDMRea.Extension` (Node 22 or later).

To use the extension: Settings, Browser, "Add to browser". It opens Chrome's or Edge's extensions page and copies the folder to load; there, turn on Developer mode, press Load unpacked and paste the folder. Browsers only install extensions without these steps from their own store.

Checks on Windows:

- `scripts/capture-windows.ps1` drives the real app through UI Automation against local test downloads and saves screenshots.
- `scripts/measure-resources.ps1` measures CPU and memory.
- `node scripts/check-extension.mjs` loads the extension in Playwright's Chromium against trap pages and checks every guard and a browser download taken over.

All three use the local server in `scripts/fixture-server.py` (needs Python 3).

There is no signed installer yet, and the extension isn't in the browser stores: it loads unpacked.

## More

- [Architecture](docs/architecture.md)
- [Verification](docs/verification.md)
- [Research provenance](docs/investigation.md)
- [Workspace review](docs/workspace-review.md)

Built by Tem Revil, with Claude as coding assistant.
