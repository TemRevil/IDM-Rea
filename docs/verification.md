# Verification history — 2026-11-03

## Current reviewed version

The original workspace recovered and was compared against the reconstructed
remote source. Confirmed transfer/control regressions and missing UI behavior
were fixed while retaining the Windows publishing and IPC improvements.
[Review details](workspace-review.md).

Application commit `2536367cc47ec2e136fe8e18658885cc05e68747` passed
[run 41638803847](https://github.com/TemRevil/IDM-Rea/actions/runs/41638803847):
29 tests on Linux, 29 tests on Windows, self-contained Windows publishing, and
actual WinUI runtime/capture verification. The runtime script additionally
checks enabled button states on completed, paused and active rows. The PNGs
were downloaded through the GitHub connector and visually inspected.
The active transfer remains visible alongside the completed and paused rows
at the default window size.

Artifacts: `IDM-Rea-windows-x64` and `IDM-Rea-runtime-screenshots`
(screenshot artifact ID `13231818308`). They contain unsigned outputs and
controlled local test downloads. No proprietary-target research files were
added to the repository.

## Original reconstruction history

The previous source tree, installed toolchain and saved CLI login were absent from the newly started workspace. This repository was reconstructed from the user handoff; previous test results do not validate it.

Local toolchain: Linux x64, .NET SDK 10.0.403. Engine build passed with zero warnings/errors. Final fresh suite: 21 passed, 0 failed, 0 skipped, including the real C# client/process test. Fresh tests exercise exact bytes and existing-file preservation, pause/restart/resume, ignored ranges, full replacement, incorrect ranges, changed validators, missing validators, extra uncheckpointed bytes, short partial files, complete checkpoints lacking publication identity, Last-Modified fallback, truncated bodies/cancel cleanup, Windows filenames, publication recovery on both sides of move, unrelated destinations, and actual C# EngineClient/process protocol and persistence. The IPC test exposed a reconnect race that was fixed by retaining a pending server instance while replying.

WinUI packages restored, but Linux cannot execute Microsoft's Windows XAML compiler (`Exec format error`). That does not establish Windows compilation success. The source was subsequently pushed to `Origins` and the remote SHA verified. Windows CI compiled and published the application and engine successfully, and all 21 tests passed on both Windows and Linux. Runtime diagnostics then identified a missing `ms-appx:///MainWindow.xaml` resource during startup; the app resource index is now named `resources.pri`. A post-publish target now copies the generated root index and compiled XAML into the final folder. Windows runtime verification passed after this correction.

`scripts/publish-windows.ps1` tests and publishes a self-contained x64 application and engine. The workflow uploads that folder before attempting runtime capture. `scripts/capture-windows.ps1` launches the actual WinUI executable, uses UI Automation to submit controlled localhost test links, captures empty/mixed queues via PrintWindow, exercises pause/resume/cancel, closes/reopens the UI, and checks transfers continue while the UI is closed. It rejects missing windows and blank captures. The hosted Windows runner supplied an interactive desktop. The complete runtime script passed in run 41635371485, producing actual WinUI PNG captures of an empty queue and controlled test downloads. Startup, adding URLs through UI Automation, progress, pause/resume/cancel, closing/reopening the UI, continued engine transfers while closed, and graceful engine shutdown were exercised. This is automated smoke verification rather than a full manual UX audit. Engine restart recovery is covered separately by the actual-client process test.

GitHub initially rejected writes even after CLI OAuth authorization. Public app metadata identified the actual credential as ChatGPT Codex Connector; its account installation was missing. After the user installed that app, a push succeeded and `ls-remote` verified commit `bda9fd034f3339e6c22e156bf35195579d373ad0` on `Origins`. Subsequent implementation checkpoints were also pushed. GitHub access is now working; no further OAuth authorization is needed.

Verified Windows run: https://github.com/TemRevil/IDM-Rea/actions/runs/41635371485 (success, application source commit `0d3aefd8b58c85ae9770a7051e90050a99fdbe66`). Build artifact: `IDM-Rea-windows-x64` (134,767,555 bytes); real-capture artifact: `IDM-Rea-runtime-screenshots` (46,368 bytes). These are unsigned self-contained x64 outputs. The workspace could not download the artifacts from their blob endpoint (HTTP 403), so the captures were not visually inspected here. Screenshot files remain available in the Actions artifact; they are not committed to source or exported into CI logs.
