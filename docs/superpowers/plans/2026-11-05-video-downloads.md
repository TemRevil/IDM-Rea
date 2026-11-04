# Video downloads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Download the video a page plays, in a chosen quality, with IDM-Rea's own engine.

**Architecture:** yt-dlp (with Deno) resolves addresses, IDM-Rea downloads streams multi-part or segment by segment, FFmpeg joins them. Tools are fetched on first use and checked against their published SHA-256 sums.

**Tech Stack:** .NET 10, xunit, Chrome MV3 extension, node:test.

**Spec:** `docs/superpowers/specs/2026-11-05-video-downloads-design.md`

## Global Constraints

- No em-dashes in shown text; no Arabic-Indic digits; no `type=number` inputs.
- Tools only from `github.com/yt-dlp/yt-dlp`, `github.com/denoland/deno`, `github.com/yt-dlp/FFmpeg-Builds` releases; a checksum mismatch installs nothing.
- Requests to a stream never exceed its chunk size (10 MB for YouTube).
- Tests never reach the internet: tool sources, the yt-dlp runner and FFmpeg are replaceable.

## Review Focus

1. An address that expires mid-download (403): resolved again with the same format, the download continues from saved bytes.
2. A tool update while a video is resolving or joining: never swapped under a running process.
3. A page with several videos, or a video element without a source yet: the button appears only on visible videos of at least 200 x 112.
4. Pause during joining, or the engine stopping during joining: the job resumes by joining again, the streams are kept until the join succeeds.
5. Cookies passed to yt-dlp: written only for the run, deleted afterwards, never logged.

---

### Task 1: Video tools (fetch, verify, install, update)

Files: `src/IDMRea.Core/VideoTools.cs`, `tests/IDMRea.Core.Tests/VideoToolsTests.cs`.
Produces: `VideoTools(string dataFolder, HttpClient http, ToolSources sources, TimeProvider time)`, `Task EnsureAsync(IProgress<ToolProgress>?, CancellationToken)`, `ToolStatus Status()`, `Task<bool> UpdateYtDlpAsync(CancellationToken)`, paths `YtDlp`, `Deno`, `Ffmpeg`, `IDisposable Use()` (blocks swaps while held).
Tests: `ToolsInstallFromTheirReleasesWhenTheChecksumsMatch`, `AChecksumMismatchInstallsNothing`, `DenoSumInPowerShellLayoutIsRead`, `YtDlpIsUpdatedAtMostOnceADayAndNeverWhileInUse`.

### Task 2: Resolver and choices

Files: `src/IDMRea.Contracts/Video.cs` (records `VideoInfo`, `VideoChoice`, `VideoJob`, `VideoStream`), `src/IDMRea.Core/VideoResolver.cs`, test JSON fixtures from the measured runs, `tests/IDMRea.Core.Tests/VideoResolverTests.cs`.
Produces: `IToolRunner.RunAsync(exe, args, token) -> (exit, stdout, stderr)`, `VideoResolver.ResolveAsync(url, headers, token) -> VideoInfo`, `VideoResolver.StreamsFor(VideoInfo, choiceId) -> (container, streams)`.
Tests: `ChoicesPreferAvcThenAv1ThenVp9PerHeight`, `MatchingSoundIsPairedWithEachPicture`, `CookiesFileExistsOnlyDuringTheRun`, `UnsupportedAddressSaysSoPlainly`, `DrmIsRefusedPlainly`.

### Task 3: Capped and fixed-file multi-part requests

Files: `src/IDMRea.Core/MultiPartTransfer.cs`, `src/IDMRea.Contracts/Protocol.cs` (ProbeResult gains `MaxRequestBytes`, `FixedFile`).
Tests: `NoRequestIsLargerThanTheCap`, `AFixedFileResumesWithoutValidators` (fixture server without ETag or Last-Modified; size only).

### Task 4: Segment transfer (HLS and DASH lists)

Files: `src/IDMRea.Core/Hls.cs` (playlist parsing), `src/IDMRea.Core/SegmentTransfer.cs`.
Tests: `MasterPlaylistListsItsVariants`, `MediaPlaylistSegmentsWithByteRangesAndInit`, `Aes128SegmentsAreDecrypted`, `SegmentsDownloadInParallelAndResumeFromTheLastCheckpoint`.

### Task 5: Video jobs in the manager

Files: `src/IDMRea.Core/VideoTransfer.cs`, `src/IDMRea.Core/DownloadManager.cs` (`AddVideoAsync`, video branch in `TransferAsync`), `src/IDMRea.Core/Ffmpeg.cs`.
Tests: `AVideoJobDownloadsBothStreamsAndJoinsThem` (fake runner and fake joiner), `AnExpiredAddressIsResolvedAgainAndContinues`, `StopDuringJoiningJoinsAgainOnResume`, `RemovingAVideoJobDeletesItsStreamParts`.

### Task 6: Engine and host commands

Files: `EngineHost.cs`, `Protocol.cs`, `src/IDMRea.BrowserHost/HostHandler.cs`.
Tests: `ProbeVideoReturnsChoices`, `ProbeVideoWhileToolsInstallReportsProgress`, `AddVideoAddsAJob`, `HostPassesVideoMessagesThrough`.

### Task 7: Extension (button, card, popup, media notes)

Files: `src/IDMRea.Extension/lib/video.js` (pure rules), `content/page.js`, `background.js`, `popup.*`, `manifest.json` (`webRequest`).
Tests (node): `aVideoQualifiesWhenVisibleAndLargeEnough`, `mediaRequestsAreRecognised`, `choiceLabelsShowQualityAndSize`.

### Task 8: App (Add dialog, details, Settings tools card)

Files: `AddDownloadDialog`, `DetailsPane`, `SettingsPage`.
Check: App builds; the real UI check passes.

### Task 9: Real check, docs, publish

- `scripts/check-video.mjs`: tools installed by the engine into `artifacts/video-check`, a real YouTube video added through the pipe, the result probed with ffprobe (both streams, duration), plus a local HLS fixture.
- README, architecture, verification; publish; relaunch the owner's copy.
