# IDM-Rea production app - implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the IDM-Rea prototype into a finished Windows 11 download manager: sections, multi-part
downloads, auto-retry, live speed, tray and notifications, settings, low resource use.

**Architecture:** Keep the four projects. Contracts gains the v2 data model and pure helpers shared by
App and Core. Core gains probe, parts, rate limit, retry and speed, and a reworked DownloadManager.
The Engine hosts IPC v2 (with a push subscription) and, on Windows only, the tray, toasts, startup
registration, Recycle Bin and memory trim. The App becomes a NavigationView shell with pages.

**Tech Stack:** C# / .NET 10, WinUI 3 (Windows App SDK 2.6.5, current stable), Microsoft.Data.Sqlite
10.0.14, xunit 2.9.3, Win32 P/Invoke (tray), Windows.UI.Notifications (toasts), PowerShell + Python
scripts.

**Spec:** `docs/superpowers/specs/2026-11-03-production-app-design.md`

**Repo:** `G:\Z\Gits\Personal\IDM-Rea` on branch `production-app`. Work only on G:. `E:\Z` is a nightly
mirror of `G:\Z` and is overwritten.

## Global Constraints

- No em-dashes in any text the app shows; use " - " or a comma.
- Digits always 0-9: format with `CultureInfo.InvariantCulture`; `Text.NormalizeDigits` on every typed number.
- `IDMRea.Core` and `IDMRea.Contracts` target `net10.0` only and must build and test on Linux.
- Engine builds `net10.0` everywhere plus `net10.0-windows10.0.19041.0` on Windows; Windows code lives in `src/IDMRea.Engine/Windows/` and compiles only for the Windows target.
- Existing 29 tests keep passing (adjust only where behaviour changes on purpose, and say so in the commit).
- Never weaken: Content-Range checks, validator checks, flush-before-checkpoint, hash-checked publication, no overwrite of an existing file.
- `jobs.db` is never written by the new engine.
- Each new behaviour's test is run and seen failing before the code that passes it.
- Commits: author Tem Revil <temrevil@gmail.com>; commits of 150+ changed lines end with `Co-Authored-By: Claude <noreply@anthropic.com>`; no other trailer. No push without the owner's word.
- Repo files contain no personal details about the owner.

## Review Focus

1. **URL with no file name** (`https://host/get?id=5`): name comes from `Content-Disposition`, else `download` plus an extension from the content type, never an empty or `get` name. Test in Task 3.
2. **Server answers the probe with 206 but a later part request with 200**: the job must drop to one stream from byte 0, never write a full body at a part offset. Test in Task 5.
3. **Disk full or save folder not writable mid-download**: Failed at once with a plain message, no retry loop. Test in Tasks 4 and 6.
4. **Empty file or file under 1 MiB**: one part, completes; a 0-byte file publishes an empty file. Test in Task 5.
5. **Link that redirects to another host**: parts request the final URL; the job keeps the original URL for display and Retry. Test in Task 3 and Task 5.

---

## File map

| Path | Responsibility |
|---|---|
| `src/IDMRea.Contracts/DownloadJob.cs` | `DownloadStatus`, `PausedBy`, `ErrorKind`, `DownloadPart`, `DownloadJob` |
| `src/IDMRea.Contracts/EngineSettings.cs` | settings record, defaults, clamping, Ultra effective values |
| `src/IDMRea.Contracts/Categories.cs` | `Category` and extension / content-type mapping |
| `src/IDMRea.Contracts/FileNames.cs` | `SafeName`, `UniquePath` (moved from Core) |
| `src/IDMRea.Contracts/Protocol.cs` | requests, responses, `ProbeResult`, pipe name, line reader |
| `src/IDMRea.Contracts/Text.cs` | sizes, speed, time left, status line, digit normalisation, error wording |
| `src/IDMRea.Core/JobStore.cs` | `jobs2.db`, migration from `jobs.db`, settings table, schema guard |
| `src/IDMRea.Core/Prober.cs` | probe request, `Content-Disposition` parsing |
| `src/IDMRea.Core/RateLimiter.cs`, `SpeedMeter.cs`, `RetryPolicy.cs`, `TransferException.cs` | transfer helpers |
| `src/IDMRea.Core/PartPlanner.cs` | initial parts and dynamic split (pure) |
| `src/IDMRea.Core/MultiPartTransfer.cs` | runs the parts of one job |
| `src/IDMRea.Core/SingleStreamTransfer.cs` | today's one-stream transfer, moved out of the manager |
| `src/IDMRea.Core/ConcurrencyGate.cs` | adjustable "downloads at once" limit |
| `src/IDMRea.Core/DownloadManager.cs` | queue, control, settings, startup rule, events |
| `src/IDMRea.Engine/EngineHost.cs` | pipe server, commands, subscriptions |
| `src/IDMRea.Engine/Program.cs` | entry point, lock file, Windows services wiring |
| `src/IDMRea.Engine/Windows/*.cs` | `TrayIcon`, `Toasts`, `StartupRegistration`, `RecycleBin`, `MemoryTrim`, `AppLauncher` |
| `src/IDMRea.App/App.xaml(.cs)` | single instance, accent and theme resources, activation args |
| `src/IDMRea.App/MainWindow.xaml(.cs)` | Mica, title bar with search, NavigationView, frame |
| `src/IDMRea.App/EngineClient.cs`, `EngineSubscription.cs` | request/response and push stream |
| `src/IDMRea.App/AppState.cs`, `DownloadRow.cs` | client-side job list, filters, counts, totals |
| `src/IDMRea.App/Views/DownloadsPage.xaml(.cs)`, `DetailsPane.xaml(.cs)`, `PartsMap.cs` | list, toolbar, details |
| `src/IDMRea.App/Views/AddDownloadDialog.xaml(.cs)` | add flow |
| `src/IDMRea.App/Views/SettingsPage.xaml(.cs)` | settings |
| `src/IDMRea.App/Assets/AppIcon.ico`, `AppIcon.png` | icon (also used by engine) |
| `scripts/make-icon.py` | renders the icon files |
| `scripts/fixture-server.py`, `capture-windows.ps1`, `measure-resources.ps1` | UI fixtures, UI check, resource check |
| `tests/IDMRea.Core.Tests/*` | existing tests + new files per task; `WindowsTests.cs` Windows target only |

---

### Task 1: Contracts v2 and shared text helpers

**Files:**
- Create: `src/IDMRea.Contracts/DownloadJob.cs`, `EngineSettings.cs`, `Categories.cs`, `FileNames.cs`, `Protocol.cs`, `Text.cs`
- Delete: `src/IDMRea.Contracts/Downloads.cs` (content moves into the files above)
- Modify: `src/IDMRea.Core/DownloadManager.cs` (`SafeName` now calls `FileNames.SafeName`; keep a forwarding `DownloadManager.SafeName` so the existing test compiles)
- Test: `tests/IDMRea.Core.Tests/ContractsTests.cs`

**Interfaces (Produces):**
```csharp
enum DownloadStatus { Queued, Downloading, Paused, Finalizing, Completed, Failed, Cancelled, Retrying }
enum PausedBy { None, Person, Engine }
enum ErrorKind { None, Network, Timeout, Server, RateLimited, ClientError, Changed, DiskFull, AccessDenied, Other }
enum Category { Video, Music, Documents, Programs, Archives, Images, Other }
enum CloseBehavior { KeepInTray, PauseAllAndQuit }
enum AppTheme { System, Light, Dark }
record DownloadPart(long Start, long End, long Received);           // End inclusive; Received from Start
record DownloadJob(Guid Id, string Url, string Destination, string PartialPath, DownloadStatus Status,
  long ReceivedBytes = 0, long? TotalBytes = null, string? ETag = null, DateTimeOffset? LastModified = null,
  string? Error = null, DateTimeOffset UpdatedAt = default, string? CompletionSha256 = null,
  Category Category = Category.Other, DateTimeOffset CreatedAt = default, DateTimeOffset? CompletedAt = null,
  int Attempts = 0, DateTimeOffset? NextRetryAt = null, ErrorKind ErrorKind = ErrorKind.None, int? HttpStatus = null,
  IReadOnlyList<DownloadPart>? Parts = null, PausedBy PausedBy = PausedBy.None, bool Resumable = false,
  string? ResolvedUrl = null) { string FileName => Path.GetFileName(Destination); }
record EngineSettings(int DownloadsAtOnce = 3, int PartsPerFile = 8, long SpeedLimitBytesPerSecond = 0,
  int RetryAttempts = 5, bool UltraPerformance = false, CloseBehavior OnClose = CloseBehavior.KeepInTray,
  bool StartWithWindows = false, bool NotifyOnComplete = true, bool NotifyOnFailure = true,
  AppTheme Theme = AppTheme.System, IReadOnlyDictionary<Category, string>? Folders = null)
  { EngineSettings Clamped(); int EffectiveParts; int BufferSize; long MinPartSize; string FolderFor(Category c);
    static string DefaultFolder(Category c); }
static class Categories { Category From(string fileName, string? contentType); string ExtensionFor(string? contentType); }
static class FileNames { string SafeName(string name); string UniquePath(string folder, string name, Func<string,bool> taken); }
record ProbeResult(string Url, string ResolvedUrl, string FileName, long? TotalBytes, bool AcceptsRanges,
  string? ETag, DateTimeOffset? LastModified, string? ContentType, Category Category, bool Resumable);
record EngineTotals(int Active, long BytesPerSecond);
record EngineRequest(string Command, string? Url = null, string? Folder = null, Guid? Id = null, int Version = 1,
  IReadOnlyList<Guid>? Ids = null, string? FileName = null, bool StartPaused = false, bool DeleteFile = false,
  EngineSettings? Settings = null);
record EngineResponse(bool Ok, IReadOnlyList<DownloadJob>? Jobs = null, string? Error = null, int Version = 1,
  bool Full = false, IReadOnlyList<Guid>? Removed = null, IReadOnlyDictionary<Guid, long>? Speeds = null,
  EngineTotals? Totals = null, EngineSettings? Settings = null, ProbeResult? Probe = null);
static class Protocol { const int CurrentVersion = 2; Json; DataDirectory; PipeName; ReadLineAsync(...) }  // unchanged members kept
static class Text { string Size(long b); string Speed(long bps); string TimeLeft(TimeSpan t);
  string StatusLine(DownloadJob job, long bps, DateTimeOffset now); string NormalizeDigits(string s);
  string Describe(ErrorKind kind, int? httpStatus); }
```

Values the spec pins:
- `Clamped()`: DownloadsAtOnce 1-10, PartsPerFile 1-16, RetryAttempts 0-10, SpeedLimit >= 0.
- `EffectiveParts` = Ultra ? 16 : PartsPerFile. `BufferSize` = Ultra ? 1 MiB : 256 KiB. `MinPartSize` = Ultra ? 512 KiB : 1 MiB.
- `DefaultFolder`: `<UserProfile>\Downloads\<Category name>`; Other = `<UserProfile>\Downloads`.
- Extensions: Video `mp4 mkv avi mov webm wmv flv m4v mpg mpeg 3gp ts`; Music `mp3 flac wav aac ogg m4a wma opus`; Documents `pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv epub md`; Programs `exe msi msix msixbundle appx appxbundle bat cmd ps1 apk dmg`; Archives `zip rar 7z tar gz tgz bz2 xz iso cab zst`; Images `jpg jpeg png gif webp bmp svg tiff heic ico`. No extension: content type prefix `video/`, `audio/`, `image/`, `application/pdf`, `application/zip` map accordingly; else Other.
- `Text.Size`: binary units, labels `B KB MB GB TB`; value < 100 shows one decimal, otherwise none (`12.4 MB`, `640 MB`, `0 B`).
- `Text.TimeLeft`: `< 60 s` → `"45 s left"`, `< 60 min` → `"1 min left"` (rounded up), else `"1 h 5 min left"`.
- `Text.StatusLine` exact copy: downloading `"12.4 MB of 640 MB - 8.2 MB/s - 1 min left"` (unknown total: `"12.4 MB - 8.2 MB/s"`), queued `"Waiting"`, paused `"Paused - 12.4 MB of 640 MB"`, retrying `"Retrying in 12 s (attempt 2 of 5)"`, finalizing `"Checking file"`, completed `"Done - 640 MB"`, failed `"Failed - " + Describe(...)` lowercase-first, cancelled `"Cancelled"`.
- `Text.Describe`: Network `"Can't reach the server. Check your connection."`; Timeout `"The server stopped responding."`; Server `"The server had a problem (HTTP 503)."`; RateLimited `"The server asked to slow down."`; ClientError 404/410 `"The server no longer has this file. Check the link."`, 401/403 `"The server refused the request."`, other `"The server refused the request (HTTP 4xx)."` with the real code; Changed `"The file changed on the server. Retry to download it again from the start."`; DiskFull `"Not enough disk space."`; AccessDenied `"Can't write to the save folder."`; Other `"Something went wrong."`.
- `NormalizeDigits` maps U+0660-0669 and U+06F0-06F9 to 0-9.
- JSON: v1 payloads (no new fields) must deserialize; new enum fields default.

- [ ] **Step 1: Write failing tests** in `ContractsTests.cs`: `SettingsClampAndUltraValues` (`new EngineSettings(DownloadsAtOnce: 40, PartsPerFile: 0, RetryAttempts: -1).Clamped()` → 10, 1, 0; Ultra → 16 / 1048576 / 524288; off → 8 / 262144 / 1048576), `CategoryFromNameAndType` (theory: `a.MKV`→Video, `b.tar.gz`→Archives, `setup.exe`→Programs, `x` + `audio/mpeg`→Music, `x` + null→Other), `SizeAndTimeText` (theory over the examples above), `StatusLineCopy` (one case per status with exact strings above), `DigitsNormalised` (`"١٢٣"` and `"۴۵"` → `"123"`, `"45"`), `V1PayloadStillReads` (the JSON one v1 job serializes to today deserializes with `Parts == null`, `Category == Other`).
- [ ] **Step 2: Run** `dotnet test tests/IDMRea.Core.Tests -c Release --filter ContractsTests` → fails to compile (types missing).
- [ ] **Step 3: Implement** the files above. `UniquePath` keeps today's `"name (n).ext"` rule.
- [ ] **Step 4: Run** the full suite → all pass (29 old + new).
- [ ] **Step 5: Commit** `feat: v2 contracts, categories, settings and status text`.

### Task 2: JobStore v2 (jobs2.db, migration, settings, schema guard)

**Files:** Modify `src/IDMRea.Core/JobStore.cs`; Test `tests/IDMRea.Core.Tests/StoreTests.cs`

**Interfaces (Produces):** `JobStore(string directory)`; `IReadOnlyList<DownloadJob> Load()`; `void Save(DownloadJob job)`; `void Delete(Guid id)`; `EngineSettings LoadSettings()` (defaults when none); `void SaveSettings(EngineSettings s)`.

Rules: database file `jobs2.db`, `PRAGMA user_version=2`, WAL, FULL sync, tables `jobs(id,payload)` and `settings(key TEXT PRIMARY KEY, value TEXT NOT NULL)` (one key `engine`). If `jobs2.db` is missing and `jobs.db` exists, copy every row (read-only open of `jobs.db`, `Mode=ReadOnly`), in one transaction. If `user_version > 2`, throw `InvalidDataException("This data was saved by a newer version of IDM-Rea.")`.

- [ ] **Step 1: Failing tests:** `MigratesV1WithoutTouchingIt` (create `jobs.db` with the v1 schema and one row; new store loads it; `jobs.db` bytes and mtime unchanged; `jobs2.db` exists), `SettingsRoundTrip`, `RefusesNewerSchema` (set `user_version=3` on `jobs2.db` → throws), `DeleteRemovesRow`.
- [ ] **Step 2: Run** → fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run full suite** → pass.
- [ ] **Step 5: Commit** `feat: jobs2 database with v1 migration and settings`.

### Task 3: Prober

**Files:** Create `src/IDMRea.Core/Prober.cs`; Modify `tests/IDMRea.Core.Tests/DownloadTests.cs` (`Fixture`); Test `tests/IDMRea.Core.Tests/ProbeTests.cs`

**Interfaces:** `static Task<ProbeResult> Prober.ProbeAsync(HttpClient http, string url, CancellationToken token)`; `static string? Prober.FileNameFrom(string? contentDisposition)`.

Rules: send `GET` with `Range: bytes=0-0`, `Accept-Encoding: identity`, read headers only and dispose. 206 with `Content-Range: bytes 0-0/N` → `TotalBytes = N`, `AcceptsRanges = true`. 200 → `TotalBytes = Content-Length`, `AcceptsRanges = false`. Other status → throw `TransferException` (Task 4 type; in this task create `TransferException` with `ErrorKind Kind`, `int? HttpStatus`, `TimeSpan? RetryAfter`). `Resumable = AcceptsRanges && TotalBytes != null && (strong ETag || LastModified != null)`. `ResolvedUrl` = final URL after redirects. File name: `filename*` (RFC 5987, UTF-8, percent-decoded) over `filename`, else last path segment unescaped, else `"download" + Categories.ExtensionFor(contentType)`; always through `FileNames.SafeName`.

Fixture changes (used by later tasks too): `Body` size settable (`BodySize`, default 1 MiB); parse `bytes=a-b` and `bytes=a-`; new `Headers` dictionary added to every response; `RedirectOnce` (first request answers `302` to `/moved/file.bin`); `Path` property; record each request's range in `Ranges` (thread-safe list).

- [ ] **Step 1: Failing tests:** `ProbeReadsSizeRangesAndValidator`, `ProbeWithoutRanges` (mode `ignore` → AcceptsRanges false, Resumable false), `FileNameFromHeaders` (theory: `attachment; filename="r.pdf"` → `r.pdf`; `attachment; filename*=UTF-8''%D9%85%D9%84%D9%81.zip` → `ملف.zip`; both present → `filename*` wins), `NoNameInUrl` (URL `/get?id=5`, no header, `Content-Type: application/zip` → `download.zip`), `RedirectGivesResolvedUrl`, `NotFoundThrowsClientError` (404 → `Kind == ClientError`, `HttpStatus == 404`).
- [ ] **Step 2: Run** → fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run full suite** → pass.
- [ ] **Step 5: Commit** `feat: probe file name, size and resumability before downloading`.

### Task 4: Rate limiter, speed meter, retry policy

**Files:** Create `RateLimiter.cs`, `SpeedMeter.cs`, `RetryPolicy.cs` (and finish `TransferException.cs`) in `src/IDMRea.Core/`; Test `tests/IDMRea.Core.Tests/HelperTests.cs`

**Interfaces:**
- `sealed class RateLimiter(TimeProvider time)` - `long BytesPerSecond { get; set; }` (0 = unlimited, change takes effect at once); `ValueTask WaitAsync(int bytes, CancellationToken token)`; one bucket shared by every connection, burst = one second's worth.
- `sealed class SpeedMeter(TimeProvider time)` - `void Add(long bytes)`; `long BytesPerSecond()` averaged over the last 3 s (0 after 3 s with no bytes).
- `static class RetryPolicy` - `ErrorKind Classify(Exception ex)` (HttpRequestException without status / SocketException / IOException from the network → Network; TimeoutException or our idle-timeout cancel → Timeout; status 408 → Timeout, 429 → RateLimited, 5xx → Server, other 4xx → ClientError; `IOException` with HResult `0x80070070` / `0x80070027` → DiskFull; `UnauthorizedAccessException` → AccessDenied; `TransferException` → its Kind); `bool IsRetryable(ErrorKind k)` (Network, Timeout, Server, RateLimited); `TimeSpan Delay(int attempt, TimeSpan? retryAfter)` → 2, 5, 15, 30, 60 s for attempts 1-5, 60 s after; `retryAfter` wins when given (capped at 10 min).

- [ ] **Step 1: Failing tests** with `FakeTimeProvider` (write a tiny one in the test project; no new package): `LimiterHoldsRateWithin10Percent` (limit 1 MiB/s, push 3 MiB in 64 KiB calls → elapsed fake time 2.7-3.3 s), `LimiterOffIsFree`, `MeterAveragesThreeSeconds`, `ClassifyAndRetryable` (theory over the mapping incl. disk-full HResult and access denied → not retryable), `BackoffSchedule`.
- [ ] **Step 2: Run** → fail. **Step 3: Implement.** **Step 4: Run full suite** → pass.
- [ ] **Step 5: Commit** `feat: shared speed limit, speed meter and retry rules`.

### Task 5: Multi-part transfer

**Files:** Create `src/IDMRea.Core/PartPlanner.cs`, `MultiPartTransfer.cs`, `SingleStreamTransfer.cs`; Modify `DownloadManager.cs` (move today's `TransferAsync` body into `SingleStreamTransfer` unchanged in behaviour); Test `tests/IDMRea.Core.Tests/PartsTests.cs`; Modify `Fixture` (modes below).

**Interfaces:**
- `static IReadOnlyList<DownloadPart> PartPlanner.Initial(long total, int maxParts, long minPartSize)` - count = clamp(total / minPartSize, 1, maxParts); equal ranges, last takes the remainder; `total == 0` → empty list.
- `static int? PartPlanner.PickToSplit(IReadOnlyList<DownloadPart> parts, IReadOnlySet<int> active)` - index of the active part with the most bytes left, only if more than 2 MiB (2,097,152) left.
- `sealed record TransferContext(HttpClient Http, RateLimiter Limiter, SpeedMeter Meter, Func<EngineSettings> Settings, Action<DownloadJob> Checkpoint)`
- `Task<DownloadJob> MultiPartTransfer.RunAsync(DownloadJob job, ProbeResult probe, TransferContext ctx, CancellationToken token)` - returns the job with all parts complete and `ReceivedBytes == TotalBytes`, before hashing.
- `Task<DownloadJob> SingleStreamTransfer.RunAsync(DownloadJob job, TransferContext ctx, CancellationToken token)` - today's logic, plus limiter, meter, pooled buffers (`ArrayPool<byte>.Shared`, size `Settings().BufferSize`).
- Both throw `TransferException` / let `RetryPolicy.Classify` handle the rest.

Rules:
- `.part` opened once with `FileOptions.Asynchronous`, `SetLength(total)`; writes with `RandomAccess.WriteAsync(handle, buffer, offset)`.
- Each part requests `Range: bytes=(Start+Received)-End` and `If-Range` with the probe validator, against `ResolvedUrl`. Its response must be 206 with matching `Content-Range` start, end and total, and the same validator; a 200 or mismatching validator → stop all parts, throw `TransferException(Changed)` if the validator differs, otherwise fall back: clear parts, run `SingleStreamTransfer` from byte 0 (Review Focus 2).
- A part's `End` may shrink while it runs (split); the worker stops reading at the new end and ends cleanly.
- Checkpoint (every 500 ms and at exit): flush the handle (`RandomAccess.FlushToDisk`), then save the job with each part's `Received` and `ReceivedBytes` = sum.
- When a worker finishes: take a pending part if any; else `PickToSplit` and, under `EffectiveParts`, split the remaining range in half and start a worker on the second half.
- 429 or 503 on a part while others run: that part goes back to pending, the job's connection cap becomes the number still running.
- A dropped connection on one part: that part reconnects after 1 s up to 3 times while other parts keep going; after that the error goes up as the job's error.
- Ultra on: all initial parts start at once; off: start one more part every 250 ms.

Fixture modes: `parts` (default range server already works), `no-ranges-on-parts` (probe gets 206, later ranged requests get 200 full body), `busy-extra` (more than 2 concurrent → 503), `drop-part-once` (first request whose range starts above 0 closes the socket after 64 KiB), `empty` (0-byte body), `SlowFirstPart` flag (applies `Delay` only to requests whose range starts at 0). Track `MaxConcurrent`.

- [ ] **Step 1: Failing tests** (8 MiB body unless noted): `PlannerInitialAndSplit` (pure: 8 MiB / 8 parts / 1 MiB → 8 ranges covering 0..8388607 exactly; 600 KiB → 1 part; 0 → empty; split picks largest > 2 MiB), `MultiPartExactBytes` (manager downloads → bytes equal, `MaxConcurrent` > 1), `MultiPartResumeAfterKill` (fixture `Delay`, pause at > 30 %, dispose manager, new manager → resumes; requested ranges after restart start at each part's saved `Start+Received`; bytes equal), `DynamicSplitHappens` (parts setting 4, initial forced to 2 parts by `MinPartSize`-sized body math: body 16 MiB with `PartPlanner.Initial(…, 2, …)`, fixture `SlowFirstPart` delays only requests whose range starts at 0 → final `Parts.Count` > 2), `FallsBackWhenPartsGet200` (bytes equal, job `Parts` cleared), `ValidatorChangeMidwayStops` (→ Failed, `ErrorKind.Changed`), `BusyServerReducesParts` (completes, no 503 leaks to the job), `DroppedPartReconnects` (completes, no Failed), `EmptyAndSmallFiles` (0 B → empty file Completed; 600 KiB → one part), `RedirectedPartsUseFinalUrl`.
- [ ] **Step 2: Run** → fail.
- [ ] **Step 3: Implement** (manager wiring is minimal here: probe, then `MultiPartTransfer` when `probe.Resumable`, else `SingleStreamTransfer`; full manager rework is Task 6).
- [ ] **Step 4: Run full suite** → pass (old single-stream tests unchanged).
- [ ] **Step 5: Commit** `feat: multi-part downloads with dynamic splitting` (co-author trailer).

### Task 6: DownloadManager rework

**Files:** Modify `src/IDMRea.Core/DownloadManager.cs`; Create `ConcurrencyGate.cs`; Test `tests/IDMRea.Core.Tests/ManagerTests.cs`; adjust `RegressionTests.cs` / `DownloadTests.cs` only where the startup rule changes (state it in the commit).

**Interfaces (Produces, used by Engine):**
```csharp
sealed record ManagerOptions(Func<string, bool>? Recycle = null, TimeProvider? Time = null, HttpMessageHandler? Handler = null, Func<long, Exception?>? WriteFault = null); // WriteFault: test seam called before each write with its file offset
DownloadManager(string directory, ManagerOptions? options = null)
event Action<DownloadJob>? JobChanged;   event Action<Guid>? JobRemoved;   event Action<DownloadJob>? JobFinished; // Completed or Failed
EngineSettings Settings { get; }
Task<ProbeResult> ProbeAsync(string url, CancellationToken token = default)
Task<DownloadJob> AddAsync(string url, string folder, string? fileName = null, bool startPaused = false)
Task ControlAsync(Guid id, bool cancel = false, bool resume = false)       // pause now records PausedBy.Person
Task RetryAsync(Guid id)        // Failed/Cancelled: resume if resumable and not Changed, else restart from 0
Task RemoveAsync(Guid id, bool deleteFile)
Task PauseAllAsync(PausedBy by) Task ResumeAllAsync()
Task ApplySettingsAsync(EngineSettings settings)   // clamps, saves, applies
IReadOnlyList<DownloadJob> Snapshot()   IReadOnlyDictionary<Guid, long> Speeds()   EngineTotals Totals()
```

Rules:
- `ConcurrencyGate` replaces `SemaphoreSlim(3)`; its limit follows `DownloadsAtOnce` immediately (raising lets queued jobs start; lowering lets running ones finish).
- Category and folder: `AddAsync` uses the probe (via `ProbeAsync`) to set `Category`, `Resumable`, `ResolvedUrl`, name (caller's `fileName` wins, through `SafeName`); destination via `FileNames.UniquePath`.
- Retry: on a retryable error and `Attempts < RetryAttempts` → status `Retrying`, `NextRetryAt = now + RetryPolicy.Delay(...)`, timer restarts it. `Attempts` resets to 0 whenever new bytes arrive. Otherwise Failed with `Error = Text.Describe(kind, status)`, `ErrorKind`, `HttpStatus`. `NetworkChange.NetworkAvailabilityChanged` (available) → every Retrying job starts now.
- Startup: Downloading, Queued, Retrying (at stored `NextRetryAt`, or now if past) and `Paused` with `PausedBy.Engine` start by themselves; `PausedBy.Person` stays paused; Finalizing recovers as today. `DisposeAsync` marks running jobs `Paused` with `PausedBy.Engine`.
- Remove: active → cancel first; partial deleted; with `deleteFile` and Completed → `Recycle(destination)` (default `File.Delete`); row deleted from store; `JobRemoved`.
- Speed limit and `BytesPerSecond` changes apply live via the shared `RateLimiter`.
- `JobChanged` fires on every save; no polling timers while idle.
- `CompletedAt` set on Completed; `CreatedAt` on add.

- [ ] **Step 1: Failing tests:** `AddUsesProbeNameCategoryAndFolder`, `DownloadsAtOnceAppliesLive` (limit 1, add 3 slow jobs → 1 Downloading; set 3 → 3 Downloading), `RetriesThenFails` (server always drops → Retrying with attempts 1..N then Failed with `"Can't reach the server. Check your connection."`, `FakeTimeProvider` advances delays), `ProgressResetsAttempts`, `RetryAfterIsHonoured` (429 `Retry-After: 7` → `NextRetryAt` = now + 7 s), `DiskFullFailsWithoutRetry` (`ManagerOptions.WriteFault` throws `IOException` with HResult `0x80070070` at 1 MiB → Failed, `ErrorKind.DiskFull`, `Attempts == 0`, message `"Not enough disk space."`), `StartupRule` (persist one each of Downloading, Paused/Person, Paused/Engine, Retrying; new manager → first, third, fourth active; second paused), `RemoveWithDeleteRecycles` (Recycle callback receives destination), `RetryAfterChangedRestartsFromZero`, `PauseAllResumeAll`.
- [ ] **Step 2: Run** → fail. **Step 3: Implement.** **Step 4: Run full suite** → pass.
- [ ] **Step 5: Commit** `feat: settings-driven queue, auto-retry, remove and restart rules` (co-author trailer).

### Task 7: Engine host and IPC v2

**Files:** Create `src/IDMRea.Engine/EngineHost.cs`; Modify `Program.cs`, `IDMRea.Engine.csproj`; Modify `tests/IDMRea.Core.Tests/DownloadTests.cs` (process test) ; Test `tests/IDMRea.Core.Tests/IpcTests.cs`

**Interfaces:** `sealed class EngineHost(DownloadManager manager, IEngineShell shell)` - `Task RunAsync(CancellationToken stop)`. `interface IEngineShell { void Notify(DownloadJob job); void SettingsChanged(EngineSettings s); bool Recycle(string path); }` with `NullShell` for Linux.

Rules:
- csproj: `<TargetFrameworks>net10.0</TargetFrameworks>`, plus `net10.0-windows10.0.19041.0` when `$([MSBuild]::IsOSPlatform('Windows'))`; Windows target `OutputType=WinExe`, `ApplicationIcon=..\IDMRea.App\Assets\AppIcon.ico`; `Windows/**` compiled only there. Workstation, non-concurrent GC (`ServerGarbageCollector=false`, `ConcurrentGarbageCollection=false`), `TieredPGO=true`, `InvariantGlobalization=true`.
- Accept connections concurrently (each served on its own task), `NamedPipeServerStream.MaxAllowedServerInstances`, keep the pending-listener trick.
- Commands: `list probe add pause resume cancel retry remove pauseAll resumeAll getSettings setSettings subscribe shutdown`; `pause/resume/remove/retry` accept `Id` or `Ids`; `pauseAll` from a request is `PausedBy.Person`. `Version` 1 or 2 accepted, other → error. `list` answers include `Speeds` and `Totals`.
- `subscribe`: connection stays open; first line `Full = true` with all jobs, settings, speeds, totals; then at most every 250 ms, only when something changed, one line with changed jobs, `Removed`, speeds of active jobs, totals. Write errors end the subscription.
- `shutdown` stops after replying; Ctrl+C / process exit disposes the manager (jobs → PausedBy.Engine).

- [ ] **Step 1: Failing tests:** `SubscribePushesFullThenDeltas` (in-process host on a temp data dir via env var; client subscribes; add a job → receives a delta with that job; idle 1 s → no lines), `V1ListStillAnswered`, `BulkPauseByIds`, `SettingsOverIpc` (set PartsPerFile 99 → reads back 16), `ConcurrentClientsDuringSubscribe` (a `list` succeeds while a subscription is open). Update the process test to the new engine path (`bin/<config>/net10.0/IDMRea.Engine.dll`).
- [ ] **Step 2: Run** → fail. **Step 3: Implement.** **Step 4: Run full suite** → pass.
- [ ] **Step 5: Commit** `feat: engine IPC v2 with live subscription` (co-author trailer).

### Task 8: Engine on Windows: tray, toasts, start with Windows, Recycle Bin, memory trim

**Files:** Create `src/IDMRea.Engine/Windows/{WindowsShell,TrayIcon,Toasts,StartupRegistration,RecycleBin,MemoryTrim,AppLauncher,Native}.cs`; Modify `Program.cs`; Modify `tests/IDMRea.Core.Tests/IDMRea.Core.Tests.csproj` (add Windows target on Windows; link the Windows files); Test `tests/IDMRea.Core.Tests/WindowsTests.cs`.

**Interfaces:** `WindowsShell : IEngineShell` owns the rest. `StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "IDM-Rea")` - `void Set(bool on, string exePath)`, `bool IsSet()`. `static bool RecycleBin.Send(string path)` (`SHFileOperationW` with `FO_DELETE | FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI`). `AppLauncher.Open(string? args)` starts `..\IDMRea.App.exe` next to the engine folder with `--job <id>` or `--add-clipboard`.

Rules:
- Tray: raw `Shell_NotifyIconW` on a dedicated STA thread with a hidden message-only window; icon from the exe's own icon resource. Tooltip `"2 downloading - 8.1 MB/s"` or `"IDM-Rea - idle"`, refreshed at most once a second while active. Menu (`TrackPopupMenu`): Open IDM-Rea, Add from clipboard, separator, Pause all, Resume all, separator, Quit. Double-click → Open. Quit → `PauseAllAsync(PausedBy.Person)` then stop.
- Toasts: register AUMID `IDMRea` under `HKCU\Software\Classes\AppUserModelId\IDMRea` (`DisplayName` "IDM-Rea", `IconUri` = `AppIcon.png` beside the app); `ToastNotificationManager.CreateToastNotifier("IDMRea")`. Completed: title file name, text "Download finished", buttons Open, Show in folder. Failed: text `Text.Describe(...)`, button Details. `Activated` → open file / `explorer /select,` / `AppLauncher.Open("--job <id>")`. Respect `NotifyOnComplete` / `NotifyOnFailure`.
- Start with Windows: `"<engine exe>" --tray`; applied on `SettingsChanged`.
- Memory trim: 10 s after the last active job ends, `GC.Collect(2, GCCollectionMode.Aggressive, true, true)` then `SetProcessWorkingSetSize(-1, -1)`; never while active.
- `--tray` start shows only the tray; a normal start (from the app) is the same process, no window ever.

- [ ] **Step 1: Failing tests (Windows target):** `StartupRegistrationRoundTrip` (test key `Software\IDMRea-Tests\Run`, cleaned up), `RecycleBinSendRemovesFile` (temp file → gone from disk, call returns true), `ToastXmlForCompletedAndFailed` (pure builder `Toasts.BuildXml(job)` contains the file name, the two / one buttons with action ids `open|<id>`, `folder|<id>`, `details|<id>`; no em-dash).
- [ ] **Step 2: Run** → fail. **Step 3: Implement.** **Step 4: Run full suite on Windows** → pass; manual: start engine, tray icon appears, menu works.
- [ ] **Step 5: Commit** `feat: tray icon, notifications, start with Windows and Recycle Bin` (co-author trailer).

### Task 9: App icon and shell (single instance, Mica, title bar, theme, navigation)

**Files:** Create `scripts/make-icon.py`, `src/IDMRea.App/Assets/AppIcon.ico`, `AppIcon.png`, `EngineSubscription.cs`, `AppState.cs`, `DownloadRow.cs`; Modify `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `EngineClient.cs`, `IDMRea.App.csproj`, `app.manifest`; Modify `scripts/publish-windows.ps1` (engine `-f net10.0-windows10.0.19041.0`, copy `AppIcon.png`).

**Interfaces:**
- `EngineSubscription` - `event Action<EngineResponse>? Received; Task StartAsync(CancellationToken)` (reconnects every 2 s if the engine restarts; `Stop()` when minimised).
- `AppState` (UI thread) - `ObservableCollection<DownloadRow> Visible`; `string Filter` (`all downloading queued completed failed` or a `Category` name); `string Search`; `SortMode Sort` (`Newest Name Size Status`); `IReadOnlyDictionary<string,int> Counts`; `EngineTotals Totals`; `EngineSettings Settings`; `void Apply(EngineResponse r)`.
- Filter rule: downloading = Downloading, Retrying, Finalizing; queued = Queued, Paused; completed = Completed; failed = Failed; Cancelled shows only under All (designer's call). Search matches file name or URL, case-insensitive.
- `DownloadRow` (bindable): `Id Name Status StatusLine Progress Indeterminate CanPause CanResume CanRetry CategoryGlyph Job`.

Rules:
- Icon: rounded square `#C46532`, white down arrow over a short white bar; sizes 16 20 24 32 40 48 64 256 in the ico, 256 px png. Script uses Pillow (install into the scratch Python env only if missing; ask before a global install).
- Single instance: `AppInstance.FindOrRegisterForKey("IDMRea.Main")`; a second launch redirects its arguments and exits; the main instance brings its window forward and handles `--job <id>` (select it, open details) and `--add-clipboard` (open the Add dialog).
- `MicaBackdrop` (falls back to solid on Windows 10), `ExtendsContentIntoTitleBar`, `TitleBar` control with icon, "IDM-Rea" and an `AutoSuggestBox` named "Search downloads".
- Accent resources in `App.xaml` both themes: `SystemAccentColor #C46532`, `Dark1 #A8552A`, `Dark2 #8C4623`, `Dark3 #6F371B`, `Light1 #D07C4F`, `Light2 #DC946C`, `Light3 #E8AE8E`.
- Theme from settings applied to the root element and title-bar buttons; the last theme is cached in `<data dir>\app-theme.txt` and applied before first paint.
- NavigationView items (AutomationProperties.Name = label): "All downloads", "Downloading", "Queued and paused", "Completed", "Failed", header "Categories", "Video", "Music", "Documents", "Programs", "Archives", "Images", "Other"; footer "Settings". Each shows its count (`InfoBadge`).
- Status bar at the bottom of the shell: `"3 downloading - 12.4 MB/s"` / `"Ready"` and engine state.
- Closing: if `OnClose == PauseAllAndQuit` send `pauseAll` then `shutdown`; then exit the process. Memory: the subscription stops while minimised.
- Remove the old hero text and the old single-page layout.

- [ ] **Step 1: Failing check:** extend `scripts/capture-windows.ps1` (Task 13 finishes it) with a first block that finds the window, every nav item name above, "Search downloads", and fails if any is missing; run against the current build → FAIL.
- [ ] **Step 2: Implement.** Build with `scripts/publish-windows.ps1`.
- [ ] **Step 3: Run the check** → PASS; screenshot both themes and look at them.
- [ ] **Step 4: Commit** `feat: app shell with navigation, Mica, title bar search and accent` (co-author trailer).

### Task 10: Downloads page and details pane

**Files:** Create `src/IDMRea.App/Views/DownloadsPage.xaml(.cs)`, `DetailsPane.xaml(.cs)`, `PartsMap.cs`; Modify `MainWindow.xaml.cs` (navigation to the page with a filter).

Rules:
- Toolbar (`CommandBar`): "Add download" (accent), "Resume all", "Pause all", sort menu "Sort" (Newest, Name, Size, Status). Bulk "Pause", "Resume", "Remove" appear when more than one row is selected.
- `ListView` `SelectionMode=Extended`. Row: category glyph (Segoe Fluent Icons: Video `E714`, Music `E8D6`, Documents `E8A5`, Programs `E7AC`, Archives `F012`, Images `EB9F`, Other `E7C3`), name, `StatusLine`, thin `ProgressBar`, actions shown on pointer-over and keyboard focus: one toggle button named "Pause" or "Resume", "Show in folder", "More" (`MenuFlyout`: Cancel, Retry, Copy link, Remove from list, Delete file).
- Empty state per filter: All `"No downloads yet."` + Add button; Downloading `"Nothing is downloading."`; Queued and paused `"Nothing is waiting."`; Completed `"Finished downloads appear here."`; Failed `"No failed downloads."`; category `"No <category> files yet."`.
- Details pane (right, 360 px, AutomationProperties.Name "Download details", close button): fields and actions exactly as the spec's Details pane; `PartsMap` draws one bar per part (filled share = Received / length, accent fill); "Delete file" opens a `ContentDialog`: title "Delete file?", text `"<name> will be moved to the Recycle Bin. You can restore it from there."`, buttons "Delete" / "Cancel".
- Keyboard: Ctrl+N, Ctrl+V (link in clipboard → Add dialog prefilled), Ctrl+F, Space, Enter, Delete, Ctrl+A. Dropping text or a link on the window opens the Add dialog with it.
- Errors from commands show in an `InfoBar` at the top of the page, closable.

- [ ] **Step 1: Failing check:** capture script block: add three fixture links through the engine pipe, then assert per row the toggle name/enabled (`complete.bin`: no toggle enabled; `paused.bin`: "Resume" enabled; `active.bin`: "Pause" enabled), select `active.bin` → "Download details" exists with "Copy link", "Show in folder"; filter "Completed" shows only `complete.bin`. Run → FAIL.
- [ ] **Step 2: Implement.** **Step 3: Run the check** → PASS; screenshots light and dark, default size 1100 x 760, look at them.
- [ ] **Step 4: Commit** `feat: downloads list with filters, bulk actions and details pane` (co-author trailer).

### Task 11: Add download dialog

**Files:** Create `src/IDMRea.App/Views/AddDownloadDialog.xaml(.cs)`.

Rules:
- `ContentDialog` title "Add download". Fields: "Download URL" (prefilled from the clipboard when it holds an http/https link), "File name" (editable), "Save to" (category folder, "Browse" button using the Windows App SDK folder picker), read-only line `"640 MB - Video - Can resume"` / `"Size unknown - Other - Can't resume"`. Probe runs 400 ms after the URL stops changing; a spinner while probing; probe errors show inline with `Text.Describe`.
- Same URL already in the list → `InfoBar` "This link is already in your list." with buttons "Open existing" / "Download again".
- If the file exists → line `"A file with this name exists. It will be saved as <unique name>."` (`FileNames.UniquePath` against the folder and the list's destinations).
- Buttons: "Start download" (primary, disabled until a valid probe), "Download later" (secondary, adds paused), "Cancel".
- Entry points: toolbar, Ctrl+N, Ctrl+V, drop, `--add-clipboard`.

- [ ] **Step 1: Failing check:** capture script: put the fixture URL `.../named` (fixture answers with `Content-Disposition: attachment; filename="report.pdf"`) in the clipboard, press Ctrl+N, wait for "File name" = `report.pdf` and the line to contain "Documents", press "Start download", wait for Completed under "Documents". Run → FAIL.
- [ ] **Step 2: Implement.** **Step 3: Run** → PASS. **Step 4: Commit** `feat: add download dialog with probe, clipboard and duplicate check`.

### Task 12: Settings page

**Files:** Create `src/IDMRea.App/Views/SettingsPage.xaml(.cs)`.

Rules:
- Sections and controls (AutomationProperties.Name = label): General - "When I close the window" (ComboBox: "Keep downloading in the tray", "Pause everything and quit"), "Start with Windows" (ToggleSwitch), "Notify when a download finishes", "Notify when a download fails"; Downloads - one row per category "Video folder" ... "Other folder" with Browse, "Downloads at once" (1-10), "Parts per file" (1-16; disabled and showing 16 with "Set by Ultra performance" while Ultra is on), "Speed limit" (ToggleSwitch + value `TextBox` with `InputScope="Number"` + unit ComboBox "KB/s" / "MB/s"), "Auto-retry attempts" (0-10); Performance - "Ultra performance" with the line "Uses up to 16 parts per file and larger buffers to download as fast as possible. The app itself stays light."; Appearance - "Theme" (System, Light, Dark); About - version, engine status, "Open data folder", "Stop engine".
- Number fields are `TextBox` (not NumberBox), run through `Text.NormalizeDigits`, clamped on commit.
- Every change sends `setSettings` at once; the page re-reads values from the engine's reply (so clamping shows).

- [ ] **Step 1: Failing check:** capture script: open Settings, set "Parts per file" to `"٢٠"` → field shows 16 after commit; turn "Ultra performance" on → "Parts per file" disabled; set Theme Light → root `RequestedTheme` Light (screenshot); set "Downloads at once" 1 → engine `getSettings` returns 1. Run → FAIL.
- [ ] **Step 2: Implement.** **Step 3: Run** → PASS. **Step 4: Commit** `feat: settings page wired to the engine` (co-author trailer).

### Task 13: Scripts, resource check, CI

**Files:** Modify `scripts/fixture-server.py`, `scripts/capture-windows.ps1`; Create `scripts/measure-resources.ps1`; Modify `.github/workflows/build.yml`.

Rules:
- Fixture server: byte ranges `a-b` and `a-`, `/complete.bin` (64 KiB), `/paused.bin`, `/active.bin` (64 MiB, slow), `/named` (Content-Disposition `report.pdf`), `/missing.bin` (404), `/big.bin` (512 MiB, fast, generated, not stored), `/drop.bin` (drops once).
- Capture script: runs on Windows PowerShell 5.1 and pwsh (no `SHA256.HashData` / `Convert.ToHexString`); all blocks from Tasks 9-12 plus closing and reopening the window while a download continues, tray icon present (`Shell_NotifyIcon` can't be read; check the engine process stays alive and the app process exits); screenshots: each nav section, details pane, Add dialog, Settings, light and dark.
- `measure-resources.ps1`: temp data dir, fixture server, engine; samples engine CPU % (of one core) and working set every 500 ms; phases: idle 20 s; 3 x `/big.bin` (Balanced); idle 20 s after finish (memory trim); app window open idle 20 s. Prints a table and exits 1 if any target is missed: engine idle < 1 % CPU and < 40 MB; engine 3 downloads < 10 % of one core; app idle < 1 % CPU.
- CI windows job: publish, capture, measure (report only, `continue-on-error: true` for measure since hosted runners vary), upload screenshots and the measure table.

- [ ] **Step 1:** Run the capture script on the Task 12 build under Windows PowerShell 5.1 → all blocks PASS. **Step 2:** Run `measure-resources.ps1` → record the numbers; if a target fails, fix the cause (not the target) or report it. **Step 3: Commit** `test: full UI check, resource check and CI`.

### Task 14: Docs, final review, owner test

**Files:** Modify `README.md`, `docs/architecture.md`, `docs/verification.md`; vault `G:\Z\Vault\Personal\IDM-Rea\{Facts,History,Pending,Commit Index}.md` and the Index row.

- [ ] **Step 1:** README: what it does now, build and run, the line "Built by Tem Revil, with Claude as coding assistant"; remove "No ... segmented transfers, tray icon" sentence. architecture.md: processes, IPC v2, parts, startup rule, data files. verification.md: today's results with real numbers.
- [ ] **Step 2:** Whole-branch review by a fresh reviewer; fix what it confirms.
- [ ] **Step 3:** Full suite, publish, capture, measure; launch the app for the owner to test.
- [ ] **Step 4:** Commit docs; update the vault. No push until the owner says so.
