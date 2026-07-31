#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

PID_FILE=".lychee_web_server.pid"

if [[ -f "$PID_FILE" ]]; then
  PID="$(cat "$PID_FILE")"
  kill "$PID" 2>/dev/null || true
  for _ in $(seq 1 20); do
    if ! kill -0 "$PID" 2>/dev/null; then
      break
    fi
    sleep 0.1
  done
  kill -9 "$PID" 2>/dev/null || true
  rm -f "$PID_FILE"
  echo "Stopped Lychee WebGL server."
else
  echo "No Lychee WebGL server pid file found."
fi
