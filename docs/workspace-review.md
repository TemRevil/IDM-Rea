# Recovered workspace comparison — 2026-11-03

Compared the recovered original `/workspace/IDM-Rea` source with remote commit
`fa7ce1e` on `Origins`, using a separate checkout. The recovered tree was archived
locally before making changes. The published reconstruction is the base for this
review; the original uncommitted tree remains intact.

## Improvements retained from the reconstruction

- The pending named-pipe listener fixes rapid reconnects and is covered by the
  real C# client/process test.
- Corrected WinUI `resources.pri`/compiled-XAML publication and runtime diagnostics.
- Windows build and real UI capture workflow, including reopen/background-transfer
  verification.
- Expanded recovery, filename and Last-Modified tests. Baseline: 21 tests passed.
- File/directory collision checks when allocating a new destination.

## Confirmed defects fixed

Seven new cases reproduced failures before their fixes:

- Resume on an active job cancelled and restarted the HTTP request. It is now a
  no-op for queued, active and terminal jobs.
- Resume downloaded an entire file before discovering an occupied destination.
  It now rejects file/directory conflicts before changing the paused job or
  starting a request.
- A response exceeding its declared range wrote excess bytes until EOF. The
  transfer now rejects an oversized read before writing those bytes.
- Unknown-length downloads completed without an observed total. Both normal
  completion and publication recovery now record the finished byte count.
- Cancel deleted a partial file but retained its byte count. Deleted bytes are
  now removed from the progress snapshot.

An additional recovery case verifies that a checkpoint beyond the known total
restarts safely instead of sending an invalid range. Full local suite: 29 tests.

## UI behavior restored

- Pause, Resume and Cancel enabled only for applicable job states.
- Unknown-length active progress is indeterminate; completed legacy jobs still
  show complete progress.
- Start is disabled until the engine is ready and during submission. Successful
  submissions clear the URL, and Enter submits it.
- Commands and polling are serialized to prevent stale responses overwriting
  newer snapshots.
- Shutdown disables the UI only after acknowledgement; errors leave it usable.
- File count and background-transfer/engine status are visible again.
- The Open downloads action returns to the header and spacing is compacted so
  the default window can show the three mixed-state fixture rows together.

The existing Windows runtime capture now also checks enabled button states on
completed, paused and active rows. Windows results must be checked on the pushed
commit; earlier successful Windows runs do not validate these UI edits.

## Recovered research

The original REA evidence and extraction script survived and remain in the
original workspace. They have not been added to this push. This comparison
restores application behavior without publishing the recovered proprietary-target
research payload.
