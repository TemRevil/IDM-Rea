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
- **Settings:** downloads at once, parts per file, a shared speed limit, retries, what closing the window does, start with Windows, notifications, theme, and per-type folders.
- **Follows Windows.** Light or dark mode and the Windows accent colour. The icon is drawn in the accent colour too.

## Build and run

Needs Windows x64 and the .NET 10 SDK.

```powershell
pwsh scripts/publish-windows.ps1          # tests, then publishes the app and engine to artifacts\windows
artifacts\windows\IDMRea.App.exe          # keep the folder together: the engine lives in engine\
```

Engine tests also run on Linux: `dotnet test tests/IDMRea.Core.Tests -c Release`.

Checks on Windows:

- `scripts/capture-windows.ps1` drives the real app through UI Automation against local test downloads and saves screenshots.
- `scripts/measure-resources.ps1` measures CPU and memory.

Both use the local server in `scripts/fixture-server.py` (needs Python 3).

There is no signed installer or browser integration yet.

## More

- [Architecture](docs/architecture.md)
- [Verification](docs/verification.md)
- [Research provenance](docs/investigation.md)
- [Workspace review](docs/workspace-review.md)

Built by Tem Revil, with Claude as coding assistant.
