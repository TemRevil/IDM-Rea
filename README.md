# IDM-Rea

Independent Windows download manager using C# and WinUI 3. Reconstructed on 2026-11-03 from a detailed handoff after the previous workspace became inaccessible. This is new source requiring fresh verification.

The design uses a separate background engine, SQLite checkpoints, validated HTTP resume, and current-user named-pipe IPC. It does not contain IDM code or proprietary binaries. Research observations in the handoff informed the design; original raw REA evidence is unavailable in this workspace and is not recreated.

Windows build: `pwsh scripts/publish-windows.ps1`. Run `artifacts/windows/IDMRea.App.exe` and keep the output folder together. The app targets Windows x64. Closing the window leaves transfers running; use the stop-engine command for a graceful shutdown.

No signed installer, browser integration, segmented transfers, tray icon, or automatic startup is implemented.
