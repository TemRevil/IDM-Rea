// Which videos on a page get the "Download video" button, which requests are a page's video, and how qualities read.
import { formatBytes } from './verdict.js';

export const MIN_WIDTH = 200;
export const MIN_HEIGHT = 112;
const MOST_NOTES = 20;

/** A player someone watches: shown, at least 200 x 112, and not a silent loop with no controls (a moving background). */
export function qualifies({ width, height, opacity = 1, hidden = false, loop = false, muted = false, controls = false }) {
  if (hidden || opacity <= 0.05) return false;
  if (width < MIN_WIDTH || height < MIN_HEIGHT) return false;
  return !(loop && muted && !controls);
}

const LIST = /\.(m3u8|mpd)$/i;
const VIDEO_FILE = /\.(mp4|m4v|webm|mov|mkv|flv|avi|wmv)$/i;
const SEGMENT = /\.(ts|m4s|m4a|m4f|aac|cmfv|cmfa|mp3|ogg|opus|wav)$/i;
const LIST_TYPE = /mpegurl|dash\+xml/i;

/** A request that is a page's video: an HLS or DASH list, or a video file. Single segments and sounds are not. */
export function isMediaRequest({ url, type, contentType = '' }) {
  let path;
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;
    path = parsed.pathname;
  } catch { return false; }
  if (LIST.test(path) || LIST_TYPE.test(contentType)) return true;
  if (SEGMENT.test(path)) return false;
  if (VIDEO_FILE.test(path) || /^video\//i.test(contentType)) return true;
  return type === 'media';
}

/** The tab's media addresses with a new one added: newest first, each once, at most 20. */
export function mediaNotes(notes, url) {
  return [url, ...notes.filter((n) => n !== url)].slice(0, MOST_NOTES);
}

/** The qualities as the card lists them: "1080p" with "MP4, 255 MB" beside it. */
export function choiceRows(choices) {
  return choices.map((c) => ({
    id: c.id,
    quality: c.audioOnly ? 'Sound only' : c.height ? `${c.height}p` : 'Video file',
    detail: [(c.container || '').toUpperCase(), c.bytes ? formatBytes(c.bytes) : ''].filter(Boolean).join(', '),
  }));
}
