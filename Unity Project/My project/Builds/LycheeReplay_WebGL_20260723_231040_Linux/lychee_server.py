#!/usr/bin/env python3
import argparse, datetime, gzip, os, posixpath, signal, sys, urllib.parse
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.dirname(os.path.abspath(__file__))
REQUEST_LOG = os.path.join(ROOT, '.lychee_web_server_requests.log')

def log_request_line(text):
    try:
        with open(REQUEST_LOG, 'a', encoding='utf-8') as handle:
            handle.write('[' + datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S') + '] ' + text + '\n')
    except OSError:
        pass

try:
    signal.signal(signal.SIGPIPE, signal.SIG_IGN)
except (AttributeError, ValueError):
    pass

class LycheeHandler(SimpleHTTPRequestHandler):
    GZIP_EXTS = ('.txt', '.json')

    def do_GET(self):
        full = self.translate_path(self.path)
        accept = self.headers.get('Accept-Encoding') or ''
        if full.endswith(self.GZIP_EXTS) and 'gzip' in accept and os.path.isfile(full):
            try:
                with open(full, 'rb') as handle:
                    raw = handle.read()
            except OSError:
                self.send_error(404, 'File not found')
                return
            data = gzip.compress(raw)
            self.send_response(200)
            self.send_header('Content-Type', self.guess_type(full))
            self.send_header('Content-Encoding', 'gzip')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            try:
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass
            return
        super().do_GET()

    def guess_type(self, path):
        request_path = urllib.parse.urlparse(path).path
        if request_path.endswith('.wasm.unityweb'):
            return 'application/wasm'
        if request_path.endswith('.framework.js.unityweb'):
            return 'application/javascript'
        if request_path.endswith('.data.unityweb'):
            return 'application/octet-stream'
        return super().guess_type(path)

    def end_headers(self):
        request_path = urllib.parse.urlparse(self.path).path
        if request_path.endswith('.unityweb'):
            self.send_header('Content-Encoding', 'gzip')
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()

    def copyfile(self, source, outputfile):
        try:
            super().copyfile(source, outputfile)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

    def translate_path(self, path):
        result = self._resolve_path(path)
        self._last_full = result
        return result

    def _resolve_path(self, path):
        request_path = urllib.parse.urlparse(path).path
        request_path = urllib.parse.unquote(request_path)
        prefix = '/litchi_delivery_replay'
        if request_path == prefix or request_path == prefix + '/':
            return os.path.join(ROOT, 'index.html')
        if request_path.startswith(prefix + '/'):
            request_path = request_path[len(prefix):]
        if request_path in ('', '/'):
            return os.path.join(ROOT, 'index.html')
        if request_path.startswith('/data/'):
            return os.path.abspath(request_path)
        clean = posixpath.normpath(request_path.lstrip('/'))
        parts = [p for p in clean.split('/') if p and p not in (os.curdir, os.pardir)]
        return os.path.join(ROOT, *parts)

    def log_request(self, code='-', size='-'):
        try:
            code_str = str(getattr(code, 'value', code))
            line = code_str + ' ' + self.requestline
            if code_str == '404':
                line += ' -> ' + getattr(self, '_last_full', '')
            log_request_line(line)
        except Exception:
            pass
        super().log_request(code, size)

    def log_error(self, fmt, *args):
        try:
            msg = fmt % args if args else fmt
            if not msg.startswith('code 404'):
                log_request_line('ERR ' + msg)
        except Exception:
            pass

class LycheeHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def handle_error(self, request, client_address):
        exc = sys.exc_info()[1]
        if isinstance(exc, (BrokenPipeError, ConnectionResetError, ConnectionAbortedError)):
            return
        super().handle_error(request, client_address)

def daemonize(pid_file, log_file):
    if not hasattr(os, 'fork'):
        raise RuntimeError('--daemon requires a POSIX platform')
    first_pid = os.fork()
    if first_pid > 0:
        os._exit(0)
    os.setsid()
    second_pid = os.fork()
    if second_pid > 0:
        os._exit(0)
    os.chdir(ROOT)
    os.umask(0o022)
    log_path = os.path.abspath(log_file or os.path.join(ROOT, '.lychee_web_server.log'))
    pid_path = os.path.abspath(pid_file or os.path.join(ROOT, '.lychee_web_server.pid'))
    log_fd = os.open(log_path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o644)
    null_fd = os.open(os.devnull, os.O_RDONLY)
    os.dup2(null_fd, 0)
    os.dup2(log_fd, 1)
    os.dup2(log_fd, 2)
    try:
        os.close(null_fd)
        os.close(log_fd)
    except OSError:
        pass
    with open(pid_path, 'w') as handle:
        handle.write(str(os.getpid()))
        handle.write('\n')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host', default='0.0.0.0')
    parser.add_argument('--port', type=int, default=9091)
    parser.add_argument('--daemon', action='store_true')
    parser.add_argument('--pid-file')
    parser.add_argument('--log-file')
    args = parser.parse_args()
    if args.daemon:
        daemonize(args.pid_file, args.log_file)
    server = LycheeHTTPServer((args.host, args.port), LycheeHandler)
    print(f'Serving {ROOT} at http://{args.host}:{args.port}/')
    server.serve_forever()

if __name__ == '__main__':
    main()
