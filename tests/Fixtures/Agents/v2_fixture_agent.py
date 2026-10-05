#!/usr/bin/env python3
"""tests/Fixtures/Agents/v2_fixture_agent.py -- agent/2 (multi-seat) fixture agents.

Each ``--mode`` drives one V2 protocol behaviour so the M7 seat-source harness can assert the
host-side ProcessMultiSeatSource handles it. Standard library only; no third-party dependencies.
The agent reads agent/2 JSONL from stdin and writes agent/2 JSONL replies on stdout.

``--log <path>`` (optional) appends one compact JSON object per received observation envelope
(carrier + observation + the emitted action line) so a test can independently verify what the seat
actually received. No subprocesses are spawned and no unrelated files are modified.
"""

import argparse
import json
import sys
import os
import subprocess
import time

PROTOCOL = "agent/2"
DEFAULT_NAME = "v2-fixture-agent"


def _configure_stdio():
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def iter_messages():
    for line in sys.stdin:
        text = line.rstrip("\r\n")
        if not text.strip():
            continue
        try:
            yield json.loads(text)
        except json.JSONDecodeError:
            continue


def action_msg(request_id, action):
    return {"type": "action", "request_id": request_id, "action": action}


def handshake(name=DEFAULT_NAME):
    """Read until an agent/2 hello and reply ready; return the hello (or None on malformed hello)."""
    for message in iter_messages():
        if message.get("type") != "hello":
            continue
        if message.get("protocol") != PROTOCOL:
            emit({"type": "ready", "protocol": message.get("protocol", "unknown"), "name": name})
            return None
        emit({"type": "ready", "protocol": PROTOCOL, "name": name})
        return message
    return None


class Logger:
    def __init__(self, path):
        self._path = path
        self._handle = open(path, "a", encoding="utf-8") if path else None

    def observe(self, message, reply):
        if self._handle is None:
            return
        record = {
            "type": "observation",
            "request_id": message.get("request_id"),
            "tick": message.get("tick"),
            "seat": message.get("seat"),
            "carrier_keys": sorted((message.get("observation") or {}).keys()),
            "observation": message.get("observation"),
            "reply": reply,
        }
        self._handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        self._handle.flush()

    def close(self):
        if self._handle is not None:
            self._handle.close()
            self._handle = None


def mode_wait(args, log):
    hello = handshake()  # hello is validated by the host codec; body is not needed here.
    if hello is None:
        return
    for message in iter_messages():
        message_type = message.get("type")
        if message_type == "episode_end":
            return
        if message_type != "observation":
            continue
        reply = action_msg(message["request_id"], {"type": "wait"})
        log.observe(message, reply)
        emit(reply)


def mode_wrong_ready(args, log):
    # Reply ready carrying a non agent/2 protocol value; the host must reject the handshake.
    for message in iter_messages():
        if message.get("type") == "hello":
            emit({"type": "ready", "protocol": "agent/9", "name": DEFAULT_NAME})
            break
    for _ in iter_messages():
        pass


def mode_wrong_request(args, log):
    hello = handshake()
    if hello is None:
        return
    for message in iter_messages():
        message_type = message.get("type")
        if message_type == "episode_end":
            return
        if message_type != "observation":
            continue
        reply = action_msg(message["request_id"] + "-WRONG", {"type": "wait"})
        log.observe(message, reply)
        emit(reply)


def mode_silent(args, log):
    hello = handshake()
    if hello is None:
        return
    for message in iter_messages():
        if message.get("type") == "episode_end":
            return
        # observation: intentionally emit nothing; the host must time out.


def mode_tree(args, log):
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"],
                             stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    with open(args.pid_file, "w", encoding="ascii") as target:
        target.write(str(child.pid))
        target.flush()
        os.fsync(target.fileno())
    if handshake() is not None:
        time.sleep(120)  # Remain alive across EOF until the owner kills the whole tree.


MODES = {
    "wait": mode_wait,
    "wrong_ready": mode_wrong_ready,
    "wrong_request": mode_wrong_request,
    "silent": mode_silent,
    "tree": mode_tree,
}


def main():
    parser = argparse.ArgumentParser(
        prog="v2_fixture_agent", description="M7 agent/2 multi-seat fixture agents.")
    parser.add_argument("--mode", required=True, help="one of %s" % (", ".join(MODES),))
    parser.add_argument("--log", default=None, help="optional path to append observation logs to")
    parser.add_argument("--pid-file", default=None)
    args = parser.parse_args()

    _configure_stdio()

    handler = MODES.get(args.mode)
    if handler is None:
        sys.stderr.write("v2_fixture_agent: unknown mode: %s\n" % (args.mode,))
        sys.stderr.flush()
        return 2

    log = Logger(args.log)
    try:
        handler(args, log)
        return 0
    except BrokenPipeError:
        return 0
    finally:
        log.close()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
