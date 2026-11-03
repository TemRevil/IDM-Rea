# Windows prototype architecture

The WinUI 3 app launches an unpackaged per-user engine from `engine/IDMRea.Engine.exe`. Closing the UI leaves the engine running. The engine holds an exclusive `engine.lock` file in its data directory; another instance exits with code 2.

Contracts contains immutable snapshots, version-1 newline-delimited JSON messages and pipe naming. The pipe name includes the first 20 uppercase SHA-256 hex characters of the UTF-8 data directory. Both ends restrict pipe access to the current user. Requests are bounded to 64 Ki characters; responses to 16 Mi characters. Commands are `list`, `add`, `pause`, `resume`, `cancel`, `shutdown`. The server retains a pending listener while answering a connection to avoid reconnect races.

Core streams one HTTP connection per job, admitting three transfers concurrently. It requests identity encoding, bounds redirects, and applies connect, headers and idle-read deadlines. It validates full/partial status, Content-Range, known total length and resume validators. A 200 response replaces partial bytes. Strong ETags are preferred; Last-Modified is a weaker fallback. No validator means restart. Segmented transfers are future work.

SQLite stores one JSON payload per job in `jobs(id TEXT PRIMARY KEY, payload TEXT NOT NULL)`, with WAL, FULL synchronous mode and schema version 1. Durable progress is saved after flushing the corresponding file writes. Recovery truncates uncheckpointed bytes and accounts for shorter partial files before resuming. Interrupted jobs remain paused until explicitly resumed.

Before publishing, Core verifies EOF/length, closes and hashes the partial file, and saves a Finalizing checkpoint with SHA-256 and size. It moves the file without overwrite, then marks Completed. Startup reconciles Finalizing records by checking partial/destination hashes. This proves identity with locally checkpointed bytes, not authenticity. Unrelated destination files are preserved.

Default storage is `%LOCALAPPDATA%\IDMRea`. `IDMREA_DATA_DIRECTORY` overrides it for isolated testing and must match across app/engine/client. The database contains download URLs; do not commit it. This prototype does not protect against a malicious process running as the same user.
