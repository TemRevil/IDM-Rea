# Historical REA investigation provenance

These are statements from the user-provided session handoff, not fresh inspection results. Original evidence JSON and extraction scripts were absent from the new workspace on 2026-11-03. No evidence IDs or raw results are reconstructed here.

The earlier session investigated Internet Download Manager 6.43 Build 17 for architecture ideas for an independent app. It reported installer SHA-256 `b8fe3da0212e4f67d09dca0abfd732b3a577e403a793489aaa9ca5ae7e02b263`, static extraction of 187 records, a native PE32 x86 `IDMan.exe`, browser extension/native-messaging components, local IPC strings, and HTTP Range/If-Range strings.

Those observations support integration and resume design hypotheses. They do not recover proprietary source, scheduling, segment counts, thread models or algorithms. The historical JavaScript semantic analysis failed; no native decompiler provider was configured, and no Windows runtime observation or benchmark was performed. The installer was not executed and Authenticode trust was not independently verified.

This reconstruction contains only independently written source. It does not include the installer, extracted binaries, bundled extension source, or invented raw REA evidence. The extractor is not recreated because the handoff does not contain enough byte-level format details to verify an equivalent implementation safely.
