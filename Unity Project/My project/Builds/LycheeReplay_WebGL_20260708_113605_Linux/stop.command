#!/bin/bash
cd "$(dirname "$0")"
PID_FILE=".lychee_web_server.pid"
if [ -f "$PID_FILE" ]; then
  kill "$(cat "$PID_FILE")" 2>/dev/null || true
  rm -f "$PID_FILE"
  echo "Stopped Lychee WebGL server."
else
  echo "No Lychee WebGL server pid file found."
fi
