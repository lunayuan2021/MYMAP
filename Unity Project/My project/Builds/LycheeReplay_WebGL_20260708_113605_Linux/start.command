#!/bin/bash
cd "$(dirname "$0")"
PORT="${PORT:-9091}"
HOST="${HOST:-0.0.0.0}"
PID_FILE=".lychee_web_server.pid"
LOG_FILE=".lychee_web_server.log"
if [ -f "$PID_FILE" ] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
  echo "Server already running on port $PORT (PID $(cat "$PID_FILE"))."
else
  rm -f "$PID_FILE"
  nohup env PYTHONUNBUFFERED=1 python3 lychee_server.py --port "$PORT" --host "$HOST" > "$LOG_FILE" 2>&1 &
  echo $! > "$PID_FILE"
  sleep 1
  if ! kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
    echo "Failed to start Lychee WebGL server. Log:"
    cat "$LOG_FILE" 2>/dev/null || true
    rm -f "$PID_FILE"
    exit 1
  fi
  echo "Started Lychee WebGL server on http://127.0.0.1:$PORT/litchi_delivery_replay/ (PID $(cat "$PID_FILE"))."
fi
echo "External replay example: http://127.0.0.1:$PORT/litchi_delivery_replay/?path=/data/results/game/replay/serve/replay.txt"
echo "4K/UHD example: http://127.0.0.1:$PORT/litchi_delivery_replay/?quality=4k"
echo "External replay + 4K: http://127.0.0.1:$PORT/litchi_delivery_replay/?path=/data/results/game/replay/serve/replay.txt&quality=4k"
open "http://127.0.0.1:$PORT/litchi_delivery_replay/"
