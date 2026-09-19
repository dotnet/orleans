#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ] || [[ "$1" != "start" && "$1" != "stop" ]]; then
  echo "Usage: $0 <start|stop> <artifact-directory>" >&2
  exit 2
fi

mode="$1"
directory="$2"
mkdir -p "$directory"
directory="$(realpath "$directory")"

if [ "$mode" = "start" ]; then
  if ! command -v tcpdump >/dev/null; then
    sudo apt-get update -qq
    sudo apt-get install --no-install-recommends -y tcpdump
  fi

  # Include all IPv6 TCP headers, since tcp[tcpflags] only addresses IPv4.
  filter='tcp port 2181 and (ip6 or (tcp[tcpflags] & (tcp-fin|tcp-rst)) != 0)'
  {
    date -u +started=%Y-%m-%dT%H:%M:%S.%NZ
    echo "format=decoded-headers-only; packet-limit=100000; duration-limit=25m; timezone=UTC"
    printf 'filter=%s\n' "$filter"
    tcpdump --version
  } > "$directory/capture-metadata.log"

  sudo -n bash -c '
    echo "$$" > "$1/collector.pid"
    awk "{print \$22}" "/proc/$$/stat" > "$1/collector.start"
    exec env TZ=UTC LC_ALL=C timeout --verbose --signal=INT --kill-after=5s 25m \
      tcpdump -nn -tttt -l -s 128 -i any -c 100000 "$2"
  ' _ "$directory" "$filter" > "$directory/headers.log" 2> "$directory/collector.log" &

  deadline=$((SECONDS + 10))
  until grep -q 'listening on' "$directory/collector.log"; do
    if [ "$SECONDS" -ge "$deadline" ]; then
      echo "ZooKeeper header capture did not become ready." >&2
      cat "$directory/collector.log" >&2
      exit 1
    fi
    sleep 0.1
  done

  date -u +ready=%Y-%m-%dT%H:%M:%S.%NZ >> "$directory/capture-metadata.log"
  {
    date -u +control-start=%Y-%m-%dT%H:%M:%S.%NZ
    nc -4 -z 127.0.0.1 2181
    nc -6 -z ::1 2181
    date -u +control-end=%Y-%m-%dT%H:%M:%S.%NZ
  } >> "$directory/capture-metadata.log"

  deadline=$((SECONDS + 5))
  until grep -Eq ' IP .*Flags \[[^]]*[FR]' "$directory/headers.log" \
    && grep -Eq ' IP6 .*Flags \[[^]]*[FR]' "$directory/headers.log"; do
    if [ "$SECONDS" -ge "$deadline" ]; then
      echo "ZooKeeper header capture did not observe the connection-close control." >&2
      exit 1
    fi
    sleep 0.1
  done
  echo "control=observed-ipv4-and-ipv6-close" >> "$directory/capture-metadata.log"
else
  if [ ! -f "$directory/collector.pid" ] || [ ! -f "$directory/collector.start" ]; then
    echo "ZooKeeper capture ownership metadata is missing." >&2
    exit 1
  fi

  sudo -n python3 - "$directory" <<'PY'
from pathlib import Path
import os
import select
import signal
import sys

directory = Path(sys.argv[1])
pid = int((directory / "collector.pid").read_text())
if pid <= 1:
    raise ValueError("Capture PID must identify its owned collector process.")
expected_start = (directory / "collector.start").read_text().strip()
try:
    descriptor = os.pidfd_open(pid)
except ProcessLookupError:
    print("Collector already exited; inspect packet-count/duration limit.")
else:
    try:
        try:
            actual_start = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
        except FileNotFoundError:
            print("Collector exited before stop.")
        else:
            if actual_start != expected_start:
                raise RuntimeError("Capture PID was reused; refusing to signal another process.")
            try:
                signal.pidfd_send_signal(descriptor, signal.SIGINT)
            except ProcessLookupError:
                print("Collector exited before the stop signal.")
            waiter = select.poll()
            waiter.register(descriptor, select.POLLIN)
            if not waiter.poll(10000):
                raise TimeoutError("Capture did not stop within ten seconds.")
    finally:
        os.close(descriptor)
PY

  date -u +stopped=%Y-%m-%dT%H:%M:%S.%NZ >> "$directory/capture-metadata.log"
  cat "$directory/collector.log"
fi
