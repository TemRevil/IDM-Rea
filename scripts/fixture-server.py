"""Controlled local test downloads for the Windows runtime capture job."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import time

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        size = 65536 if self.path == '/complete.bin' else 64 * 1024 * 1024
        offset = int(self.headers.get('Range', 'bytes=0-').split('=')[1].split('-')[0])
        self.send_response(206 if offset else 200)
        self.send_header('Content-Length', str(size - offset))
        self.send_header('ETag', '"fixture-v1"')
        if offset:
            self.send_header('Content-Range', f'bytes {offset}-{size-1}/{size}')
        self.end_headers()
        try:
            while offset < size:
                block = b'IDMRea fixture!\n' * 1024
                block = block[:min(len(block), size-offset)]
                self.wfile.write(block)
                self.wfile.flush()
                offset += len(block)
                if size > 65536:
                    time.sleep(.025)
        except (ConnectionError, OSError):
            pass

ThreadingHTTPServer(('127.0.0.1', 18764), Handler).serve_forever()
