"""Local test downloads for the Windows UI check and the resource check. Standard library only.

  /complete.bin  64 KiB             /named       64 KiB, sent as "report.pdf"
  /paused.bin    64 MiB, slow       /active.bin  64 MiB, slow
  /big.bin       512 MiB, fast      /drop.bin    1 MiB, the first request is cut off halfway
  /missing.bin   404                /vanish.bin  64 KiB for the first request (the probe), then 404

Byte ranges "a-b" and "a-" are honoured, with a strong ETag, so downloads can resume and split into parts.
"Slow" is a shared budget per file (about 640 KB/s in all), so extra parts do not make it finish sooner.
The body is generated, never stored: byte n is PATTERN[n % 16].
"""
from email.utils import formatdate
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import re
import sys
import threading
import time

KIB, MIB = 1024, 1024 * 1024
PATTERN = b'IDMRea fixture!\n'
FILES = {
    '/complete.bin': (64 * KIB, False), '/named': (64 * KIB, False),
    '/paused.bin': (64 * MIB, True), '/active.bin': (64 * MIB, True),
    '/big.bin': (512 * MIB, False), '/drop.bin': (1 * MIB, False), '/vanish.bin': (64 * KIB, False),
}
BUFFER = PATTERN * (MIB // len(PATTERN) + 1)
SLOW_BLOCK, SLOW_GAP = 16 * KIB, 0.025
MODIFIED = formatdate(time.time() - 3600, usegmt=True)
lock = threading.Lock()
next_slot = {}
dropped = set()
seen = set()


def wait_turn(path):
    """Spaces blocks of a slow file 25 ms apart across all its connections."""
    with lock:
        now = time.monotonic()
        slot = max(now, next_slot.get(path, now))
        next_slot[path] = slot + SLOW_GAP
    if slot > now:
        time.sleep(slot - now)


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *args):
        pass

    def do_HEAD(self):
        self.respond(body=False)

    def do_GET(self):
        self.respond(body=True)

    def respond(self, body):
        path = self.path.split('?')[0]
        with lock:
            gone = path == '/vanish.bin' and path in seen
            seen.add(path)
        if path not in FILES or gone:
            self.send_response(404)
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        size, slow = FILES[path]
        start, end, partial = 0, size - 1, False
        match = re.fullmatch(r'bytes=(\d+)-(\d*)', self.headers.get('Range', ''))
        if match:
            start = int(match.group(1))
            end = min(int(match.group(2)), size - 1) if match.group(2) else size - 1
            if start >= size or start > end:
                self.send_response(416)
                self.send_header('Content-Range', f'bytes */{size}')
                self.send_header('Content-Length', '0')
                self.end_headers()
                return
            partial = True
        self.send_response(206 if partial else 200)
        self.send_header('Content-Type', 'application/pdf' if path == '/named' else 'application/octet-stream')
        self.send_header('Content-Length', str(end - start + 1))
        self.send_header('Accept-Ranges', 'bytes')
        self.send_header('ETag', '"fixture-v2"')
        self.send_header('Last-Modified', MODIFIED)
        if path == '/named':
            self.send_header('Content-Disposition', 'attachment; filename="report.pdf"')
        if partial:
            self.send_header('Content-Range', f'bytes {start}-{end}/{size}')
        self.end_headers()
        if not body:
            return
        cut = None
        with lock:
            if path == '/drop.bin' and path not in dropped and end - start + 1 > 2:
                dropped.add(path)
                cut = start + (end - start + 1) // 2
        position = start
        try:
            while position <= end:
                if cut is not None and position >= cut:
                    self.close_connection = True
                    self.connection.shutdown(2)
                    return
                block = SLOW_BLOCK if slow else 256 * KIB
                stop = min(end + 1, position + block, cut if cut is not None else end + 1)
                offset = position % len(PATTERN)
                if slow:
                    wait_turn(path)
                self.wfile.write(memoryview(BUFFER)[offset:offset + stop - position])
                position = stop
        except (ConnectionError, OSError):
            self.close_connection = True


port = int(sys.argv[1]) if len(sys.argv) > 1 else 18764
ThreadingHTTPServer(('127.0.0.1', port), Handler).serve_forever()
