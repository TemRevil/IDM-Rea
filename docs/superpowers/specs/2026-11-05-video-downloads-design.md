# Video downloads - design

Date: 2026-11-05. Status: approved in conversation (approach, tools, downloader); details below are the builder's calls unless marked as the owner's.

## Intent

People want the video a page is playing, the way IDM offers "Download this video": a button on the player, a choice of quality, and a fast download. Today the extension only finds links to files, so YouTube and most video sites show nothing.

## Decisions made with the owner

- **Mix of own code and community tools.** IDM-Rea's own code catches videos that pages play as plain files or standard streams. yt-dlp (with Deno, which yt-dlp needs for YouTube since 2025.11) finds the real addresses on YouTube and about 1,800 other sites. FFmpeg joins picture and sound.
- **The app fetches the tools itself** on first use, from their official GitHub releases, checks their published SHA-256 sums, and keeps yt-dlp current. Nothing is bundled.
- **IDM-Rea's own engine downloads** the streams (multi-part, speed limit, pause, resume, retry), yt-dlp only resolves addresses.

## Facts measured on 2026-11-05 (yt-dlp 2026.10.26, Deno 2.9.8)

- `yt-dlp -J --no-playlist --js-runtimes deno:PATH URL` returns every format with a direct `https` address, `filesize`, codecs, height and `downloader_options.http_chunk_size = 10485760` for YouTube. Live or HLS-only formats come as `m3u8_native`.
- YouTube's video servers answer ranges with `206`, `Content-Range: bytes a-b/size` and `Last-Modified`. They ignore `If-Range`; each address names one fixed file (`clen`, `lmt` in the address), so a known size is the identity check.
- One 40 MB range ran at 0.39 MB/s; 10 MB ranges ran at line speed (3.7 MB/s here, the line itself measured 3.0 MB/s). Requests must be capped at the chunk size.
- Release files: `yt-dlp.exe` (24 MB) with `SHA2-256SUMS`; `deno-x86_64-pc-windows-msvc.zip` (45 MB) with `.zip.sha256sum` (PowerShell `Get-FileHash` layout); FFmpeg-Builds `ffmpeg-master-latest-win64-gpl-shared.zip` (91 MB) with `checksums.sha256`. First-time setup is about 160 MB.

## Goals

1. A "Download video" button over a playing video on any site, with a quality menu (for example 1080p MP4, 720p, audio only), sizes shown.
2. The toolbar menu lists the video on the page with the same choices.
3. The app's Add dialog accepts a video page address and offers the same choices.
4. A video job downloads its picture and sound streams with the multi-part engine (requests capped at the stream's chunk size), joins them with FFmpeg without re-encoding, and publishes like any other file (zone mark, unique name, Completed).
5. Pause, resume after restart, retry, speed limit and the details pane work for video jobs. An expired address is resolved again with the same format and the download continues from its saved bytes.
6. Standard streams (HLS `.m3u8`, DASH segment lists) are downloaded segment by segment in parallel by IDM-Rea, AES-128 HLS included, then joined into MP4.
7. Settings shows the video tools' state with "Update now".

## Non-goals

- DRM-protected video (Widevine). It is refused plainly.
- Playlists and whole channels. One video per action.
- Re-encoding or converting formats.

## Anti-goals

- Running a tool whose checksum did not match, or one fetched from anywhere but its official release.
- A video button on tiny or decorative videos (backgrounds, previews under 200 x 112).
- A first-time setup with no progress shown.

## How it fits

- **Tools (`VideoTools`, Core):** `<data>\tools\yt-dlp.exe`, `deno\deno.exe`, `ffmpeg\bin\ffmpeg.exe`. Each is downloaded to a `.new` file, checked, then swapped in. yt-dlp is checked for a newer release at most once a day, and only replaced when no video is being resolved. Progress is reported to the app and the extension.
- **Resolver (`VideoResolver`, Core):** runs yt-dlp through a runner that tests can replace, parses the JSON into `VideoInfo` (title, thumbnail, duration, choices). Choices: per height the most compatible video (AVC, then AV1, then VP9) paired with matching sound (M4A for MP4, Opus for WebM), plus files that already hold both, plus audio only. Browser cookies go to yt-dlp as a Netscape cookie file in the data folder's temp area, deleted after the run.
- **Job:** `DownloadJob.Video` holds the page address, format IDs, container and its streams (address, headers, size, parts, part file, chunk size, protocol, done). The job's received and total bytes are the streams' sums.
- **Transfer (`VideoTransfer`, Core):** each stream runs through `MultiPartTransfer` (new: a per-request byte cap, and a "fixed file" mode where the size alone identifies the file) or the new `SegmentTransfer`. Then FFmpeg (`-c copy`) joins into the job's part file, and `DownloadManager` publishes as today.
- **Engine commands:** `probeVideo` (address, cookies, user agent, referrer, media addresses the browser saw) returns `VideoInfo` or tool progress; `addVideo` (address, choice) adds the job; `videoTools` and `updateVideoTools` for Settings.
- **Extension:** the content script finds visible `<video>` elements and shows the button and quality card; the service worker notes media requests per tab (`webRequest`, observe only) so direct and HLS videos on unknown sites still resolve; the popup gets a "Video on this page" section.

## Builder's calls

- Unsupported page address: yt-dlp is tried on the media addresses the browser saw, then those are probed directly.
- Container: MP4 when picture and sound are AVC or AV1 with AAC; WebM for VP9 with Opus; MKV for any other mix.
- File name: the video title, made safe, plus the quality, for example `Big Buck Bunny (1080p).mp4`.
- Part files for streams sit beside the job's part file, named with the job ID and format ID, and are deleted after joining.
