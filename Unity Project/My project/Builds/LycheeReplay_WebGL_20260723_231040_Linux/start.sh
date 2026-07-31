#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

PORT="${PORT:-9091}"
HOST="${HOST:-0.0.0.0}"
NO_OPEN="${NO_OPEN:-0}"
PID_FILE=".lychee_web_server.pid"
LOG_FILE=".lychee_web_server.log"
URL="http://127.0.0.1:${PORT}"

if [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
  echo "Server already running on port ${PORT} (PID $(cat "$PID_FILE"))."
else
  rm -f "$PID_FILE"
  : > "$LOG_FILE"
  python3 lychee_server.py --daemon --pid-file "$PID_FILE" --log-file "$LOG_FILE" --port "$PORT" --host "$HOST"
  for _ in $(seq 1 30); do
    if [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
      break
    fi
    sleep 0.2
  done
  if [[ ! -f "$PID_FILE" ]] || ! kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
    echo "Failed to start Lychee WebGL server. Log:"
    cat "$LOG_FILE" 2>/dev/null || true
    rm -f "$PID_FILE"
    exit 1
  fi
  if ! curl -fsS "${URL}" >/dev/null 2>&1; then
    echo "Lychee WebGL server process started, but HTTP health check failed. Log:"
    cat "$LOG_FILE" 2>/dev/null || true
    kill "$(cat "$PID_FILE")" 2>/dev/null || true
    rm -f "$PID_FILE"
    exit 1
  fi
  echo "Started Lychee WebGL server on ${URL} (PID $(cat "$PID_FILE"))."
fi

echo "Default: ${URL}"
echo "Map switch: ${URL}?map=map_1"
echo "Selector: ${URL}?map=select"
echo "External replay: ${URL}?path=/data/results/game/replay/serve/replay.txt"
echo "Map + external replay: ${URL}?map=map_1&path=/data/results/game/replay/serve/replay.txt"
echo "Performance mode: ${URL}?quality=performance"
echo "4K/UHD: ${URL}?quality=4k"

if [[ "$NO_OPEN" == "1" ]]; then
  exit 0
elif command -v xdg-open >/dev/null 2>&1; then
  xdg-open "$URL" >/dev/null 2>&1 || true
elif command -v open >/dev/null 2>&1; then
  open "$URL" >/dev/null 2>&1 || true
fi
