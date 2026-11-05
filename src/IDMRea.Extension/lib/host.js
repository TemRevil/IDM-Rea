// Talking to IDM-Rea through the browser's native messaging.

export const RETRY_MS = 1500;

/**
 * Sends once, and once more after a pause when the browser couldn't reach IDM-Rea at all: the host is briefly missing
 * while IDM-Rea is being updated. An answer from IDM-Rea itself, even a refusal, is final.
 */
export async function askHost(send, pause) {
  const first = await send();
  if (first?.connected !== false) return first;
  await pause();
  return send();
}
