#!/usr/bin/env python3
"""Stop an existing OBS recording when Buddy finishes, or after a hard deadline.

Requires websocket-client (`python3 -m pip install websocket-client`). Does not
start a recording, modify OBS settings, or store the WebSocket password.
"""

import argparse
import base64
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import uuid


def obs_config_path():
    if sys.platform == "darwin":
        return Path.home() / "Library/Application Support/obs-studio/plugin_config/obs-websocket/config.json"
    if sys.platform == "win32":
        return Path(os.environ["APPDATA"]) / "obs-studio/plugin_config/obs-websocket/config.json"
    return Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "obs-studio/plugin_config/obs-websocket/config.json"


def trace_directory():
    if sys.platform == "darwin":
        return Path.home() / "Library/Application Support/SlayTheSpire2/spire-buddy/runs"
    if sys.platform == "win32":
        return Path(os.environ["APPDATA"]) / "SlayTheSpire2/spire-buddy/runs"
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "godot/app_userdata/SlayTheSpire2/spire-buddy/runs"


def trace_events(path):
    """Only inspect the end of a potentially large JSONL trace."""
    try:
        with path.open("rb") as stream:
            stream.seek(0, 2)
            stream.seek(max(0, stream.tell() - 65536))
            if stream.tell():
                stream.readline()  # Discard a partial first line.
            lines = stream.readlines()
    except OSError:
        return []
    events = []
    for line in lines:
        try:
            event = json.loads(line).get("event")
        except (ValueError, AttributeError):
            continue  # An incomplete write or a non-event trace record.
        if isinstance(event, str):
            events.append(event)
    return events


class BuddyTrace:
    def __init__(self, directory):
        self.directory = directory
        existing = sorted(directory.glob("*.jsonl"))
        self.known = set(existing)
        self.active = None
        if existing:
            latest = max(existing, key=lambda path: path.stat().st_mtime)
            events = trace_events(latest)
            try:
                with latest.open("rb") as stream:
                    started = json.loads(stream.readline()).get("event") == "play_started"
            except (OSError, ValueError):
                started = False
            if started and not any(e in events for e in ("stop_requested", "play_finished")):
                self.active = latest

    def stopped(self):
        new = set(self.directory.glob("*.jsonl")) - self.known
        self.known.update(new)
        if new:
            self.active = max(new, key=lambda path: path.stat().st_mtime)
        if self.active is None:
            return False
        return any(e in trace_events(self.active) for e in ("stop_requested", "play_finished"))


def connect_obs(config):
    try:
        import websocket
    except ImportError as error:
        raise RuntimeError("Install websocket-client: python3 -m pip install websocket-client") from error

    if not config.get("server_enabled"):
        raise RuntimeError("Enable the WebSocket server in OBS → Tools → obs-websocket Settings first.")
    socket = websocket.create_connection(f"ws://127.0.0.1:{int(config.get('server_port', 4455))}", timeout=5)
    try:
        hello = json.loads(socket.recv())
        if hello.get("op") != 0:
            raise RuntimeError("OBS did not send a WebSocket Hello.")
        identify = {"rpcVersion": 1}
        auth = hello["d"].get("authentication")
        if auth:
            password = config.get("server_password", "")
            if not password:
                raise RuntimeError("OBS WebSocket authentication is enabled but no password was found.")
            secret = base64.b64encode(hashlib.sha256((password + auth["salt"]).encode()).digest()).decode()
            identify["authentication"] = base64.b64encode(hashlib.sha256((secret + auth["challenge"]).encode()).digest()).decode()
        socket.send(json.dumps({"op": 1, "d": identify}))
        if json.loads(socket.recv()).get("op") != 2:
            raise RuntimeError("OBS WebSocket authentication failed.")
        return socket
    except Exception:
        socket.close()
        raise


def obs_request(socket, request_type):
    request_id = uuid.uuid4().hex
    socket.send(json.dumps({"op": 6, "d": {"requestType": request_type, "requestId": request_id}}))
    while True:
        response = json.loads(socket.recv())
        if response.get("op") == 7 and response["d"].get("requestId") == request_id:
            result = response["d"]
            if not result["requestStatus"]["result"]:
                raise RuntimeError(f"OBS {request_type} failed: {result['requestStatus'].get('comment', 'unknown error')}")
            return result.get("responseData", {})


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--max-minutes", type=float, default=180, help="Hard limit from watcher start (default: 180)")
    parser.add_argument("--poll-seconds", type=float, default=1)
    parser.add_argument("--obs-config", type=Path, default=obs_config_path())
    parser.add_argument("--traces", type=Path, default=trace_directory())
    args = parser.parse_args(argv)
    if args.max_minutes <= 0 or args.poll_seconds <= 0:
        parser.error("--max-minutes and --poll-seconds must be positive")
    config = json.loads(args.obs_config.read_text())
    with closing(connect_obs(config)) as socket:
        if not obs_request(socket, "GetRecordStatus").get("outputActive"):
            print("OBS is not recording; nothing to watch.")
            return 0
    watch = BuddyTrace(args.traces)
    deadline = time.monotonic() + args.max_minutes * 60
    print(f"Watching Buddy; recording will stop by {args.max_minutes:g} minutes at the latest. Ctrl-C cancels this watcher only.", flush=True)
    while True:
        reason = "Buddy stopped or finished" if watch.stopped() else "time limit" if time.monotonic() >= deadline else None
        if reason:
            with closing(connect_obs(config)) as socket:
                if obs_request(socket, "GetRecordStatus").get("outputActive"):
                    obs_request(socket, "StopRecord")
                    print(f"Stopped OBS recording: {reason}.")
                else:
                    print("OBS recording was already stopped.")
            return 0
        time.sleep(args.poll_seconds)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(f"OBS watcher: {error}", file=sys.stderr)
        sys.exit(1)
