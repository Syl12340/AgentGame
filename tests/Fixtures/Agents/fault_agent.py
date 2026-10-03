#!/usr/bin/env python3
"""tests/Fixtures/Agents/fault_agent.py -- M3 fault-injection fixture agents.

Each ``--mode`` drives one distinct protocol fault so the runtime test harness
(Runner / process-session layer) can assert it is handled without hanging or
corrupting unrelated state. ``--marker <text>`` supplies the exact ready-name
for the ``argv`` mode. Slow/hang modes only sleep where the host is expected to
kill the process first; no subprocesses are spawned and no secrets are read,
and no unrelated files are modified.
"""

import argparse
import json
import random
import sys
import time

PROTOCOL = "agent/1"
DEFAULT_NAME = "fault-agent"
TAIL_MARKER = "TAIL_MARKER"

# success is TEST-ONLY, hardcoded 13-step manual route.
SUCCESS_ROUTE = [
    ("move", "east"),
    ("pickup", None),
    ("move", "east"),
    ("interact", "east"),
    ("move", "east"),
    ("move", "east"),
    ("move", "east"),
    ("pickup", None),
    ("move", "west"),
    ("move", "west"),
    ("move", "west"),
    ("move", "west"),
    ("move", "west"),
]


def _configure_stdio():
    """Force strict UTF-8 text encoding on stdio regardless of locale."""
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def emit(obj):
    """Emit one protocol JSON line on stdout (UTF-8 safe) and flush."""
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def emit_raw(buf):
    """Emit raw bytes directly to the stdout binary buffer (no text re-encode)."""
    sys.stdout.buffer.write(buf)
    sys.stdout.buffer.flush()


def action_msg(request_id, action):
    return {"type": "action", "request_id": request_id, "action": action}


def iter_messages():
    """Yield non-blank JSON messages from stdin, tolerating bad lines."""
    for line in sys.stdin:
        text = line.rstrip("\r\n")
        if not text.strip():
            continue
        try:
            yield json.loads(text)
        except json.JSONDecodeError:
            continue


def handshake(name=DEFAULT_NAME):
    """Read until ``hello`` and reply ``ready``; return True on success."""
    for message in iter_messages():
        if message.get("type") == "hello":
            emit({"type": "ready", "protocol": PROTOCOL, "name": name})
            return True
    return False


def mode_wait(_args):
    """Ordinary ready, then a legal ``wait`` action per observation."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "observation":
            emit(action_msg(message["request_id"], {"type": "wait"}))
        elif message.get("type") == "episode_end":
            return


def mode_success(_args):
    """TEST-ONLY: hardcoded 13-step manual route, then wait actions."""
    if not handshake():
        return
    step = 0
    for message in iter_messages():
        message_type = message.get("type")
        if message_type == "episode_end":
            return
        if message_type != "observation":
            continue
        request_id = message["request_id"]
        if step < len(SUCCESS_ROUTE):
            kind, direction = SUCCESS_ROUTE[step]
            step += 1
            action = {"type": kind}
            if direction is not None:
                action["direction"] = direction
            emit(action_msg(request_id, action))
        else:
            emit(action_msg(request_id, {"type": "wait"}))


def mode_success_trace(_args):
    """TEST-ONLY: the hardcoded route plus a stderr marker when episode_end arrives.

    The marker exists so the host-side tests can prove whether a terminal envelope was
    really delivered to the Agent (for example when authority recording failed mid-episode).
    """
    if not handshake():
        return
    step = 0
    for message in iter_messages():
        message_type = message.get("type")
        if message_type == "episode_end":
            sys.stderr.write("fault-agent: episode_end received\n")
            sys.stderr.flush()
            return
        if message_type != "observation":
            continue
        request_id = message["request_id"]
        if step < len(SUCCESS_ROUTE):
            kind, direction = SUCCESS_ROUTE[step]
            step += 1
            action = {"type": kind}
            if direction is not None:
                action["direction"] = direction
            emit(action_msg(request_id, action))
        else:
            emit(action_msg(request_id, {"type": "wait"}))


def mode_wrong_id(_args):
    """Reply with a request_id that does not match the observation's."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "observation":
            request_id = message["request_id"]
            emit(action_msg(str(request_id) + "-WRONG", {"type": "wait"}))
        elif message.get("type") == "episode_end":
            return


def mode_duplicate(_args):
    """Write the same action twice inside a single flushed block."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "observation":
            payload = json.dumps(
                action_msg(message["request_id"], {"type": "wait"}),
                ensure_ascii=False,
            )
            sys.stdout.write(payload + "\n" + payload + "\n")
            sys.stdout.flush()
        elif message.get("type") == "episode_end":
            return


def mode_partial(_args):
    """Emit half a valid action (no trailing newline), then sleep."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") != "observation":
            if message.get("type") == "episode_end":
                return
            continue
        payload = json.dumps(
            action_msg(message["request_id"], {"type": "wait"}),
            ensure_ascii=False,
        )
        emit_raw(payload[: len(payload) // 2].encode("utf-8"))
        time.sleep(600)
        return


def mode_silent(_args):
    """Complete the handshake, then never answer any observation."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "episode_end":
            return
        # observation: intentionally emit nothing.


def mode_oversized(_args):
    """Emit a single line longer than 64 KiB (no newline), then sleep."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") != "observation":
            if message.get("type") == "episode_end":
                return
            continue
        payload = json.dumps(message["request_id"])
        blob = (
            '{"type":"action","request_id":%s,"action":{"type":"wait"},'
            '"pad":"%s"}' % (payload, "x" * 70000)
        )
        emit_raw(blob.encode("utf-8"))
        time.sleep(600)
        return


def mode_invalid_utf8(_args):
    """Emit a line of raw, ill-formed bytes followed by a newline."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") != "observation":
            if message.get("type") == "episode_end":
                return
            continue
        emit_raw(b"\xff\xfe\x80\x81\x1b\x07ill-formed\xff\n")
        time.sleep(600)
        return


def mode_blank(_args):
    """Emit a bare newline (blank line) after the handshake."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") != "observation":
            if message.get("type") == "episode_end":
                return
            continue
        emit_raw(b"\n")
        time.sleep(600)
        return


def mode_invalid_action(_args):
    """Emit an action whose type is not a legal action."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "observation":
            emit(action_msg(message["request_id"], {"type": "unknown"}))
        elif message.get("type") == "episode_end":
            return


def mode_wrong_ready(_args):
    """Reply to hello with a ready carrying a wrong protocol value."""
    for message in iter_messages():
        if message.get("type") == "hello":
            emit({"type": "ready", "protocol": "agent/9", "name": DEFAULT_NAME})
            break
    for _ in iter_messages():
        pass


def mode_exit_before_ready(_args):
    """Exit non-zero without ever sending ready."""
    sys.stderr.write("fault_agent: exit-before-ready\n")
    sys.stderr.flush()
    sys.exit(7)


def mode_exit_after_ready(_args):
    """Send ready, then exit non-zero on the first observation."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "observation":
            sys.stderr.write("fault_agent: exit-after-ready\n")
            sys.stderr.flush()
            sys.exit(7)
        elif message.get("type") == "episode_end":
            return


def mode_stderr_flood(_args):
    """Flood >1 MiB to stderr (ending in TAIL_MARKER) before each wait reply."""
    if not handshake():
        return
    block = "x" * (1 << 20)  # 1 MiB
    for message in iter_messages():
        message_type = message.get("type")
        if message_type == "episode_end":
            return
        if message_type != "observation":
            continue
        sys.stderr.write(block)
        sys.stderr.write(TAIL_MARKER + "\n")
        sys.stderr.flush()
        emit(action_msg(message["request_id"], {"type": "wait"}))


def mode_no_read(_args):
    """Never read stdin at all; sleep until the host kills us."""
    time.sleep(600)


def mode_no_newline_eof(_args):
    """Write a valid action JSON with no trailing newline, then exit."""
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "episode_end":
            return
        if message.get("type") == "observation":
            payload = json.dumps(
                action_msg(message["request_id"], {"type": "wait"}),
                ensure_ascii=False,
            )
            sys.stdout.write(payload)  # intentional: no trailing \n
            sys.stdout.flush()
            return  # normal exit; EOF reaches an unterminated final line


def mode_ignore_shutdown(_args):
    """Refuse prompt shutdown: sleep 60s after episode_end / EOF."""
    handshake()  # ignore a missing hello; fall through to drain/sleep
    for message in iter_messages():
        if message.get("type") == "episode_end":
            time.sleep(60)
            return
        if message.get("type") == "observation":
            emit(action_msg(message["request_id"], {"type": "wait"}))
    time.sleep(60)  # EOF without episode_end: still delay cleanup


def mode_argv(args):
    """Use --marker as the exact ready name, then act like the wait mode."""
    name = args.marker if args.marker is not None else DEFAULT_NAME
    if not handshake(name=name):
        return
    for message in iter_messages():
        if message.get("type") == "observation":
            emit(action_msg(message["request_id"], {"type": "wait"}))
        elif message.get("type") == "episode_end":
            return


MODES = {
    "wait": mode_wait,
    "success": mode_success,
    "wrong-id": mode_wrong_id,
    "duplicate": mode_duplicate,
    "partial": mode_partial,
    "silent": mode_silent,
    "oversized": mode_oversized,
    "invalid-utf8": mode_invalid_utf8,
    "blank": mode_blank,
    "invalid-action": mode_invalid_action,
    "wrong-ready": mode_wrong_ready,
    "exit-before-ready": mode_exit_before_ready,
    "exit-after-ready": mode_exit_after_ready,
    "stderr-flood": mode_stderr_flood,
    "no-read": mode_no_read,
    "no-newline-eof": mode_no_newline_eof,
    "ignore-shutdown": mode_ignore_shutdown,
    "argv": mode_argv,
    "success-trace": mode_success_trace,
}

def mode_valid_then_wrong(_args):
    if not handshake():
        return
    previous = None
    for message in iter_messages():
        if message.get("type") == "episode_end":
            return
        if message.get("type") == "observation":
            request_id = message["request_id"] if previous is None else previous
            emit(action_msg(request_id, {"type": "wait"}))
            previous = message["request_id"]

def mode_privacy(_args):
    if not handshake():
        return
    for message in iter_messages():
        if message.get("type") == "episode_end":
            return
        if message.get("type") != "observation":
            raise RuntimeError("Unexpected host envelope")
        assert set(message) == {"type", "tick", "request_id", "observation"}
        observation = message["observation"]
        assert set(observation).issubset({"position", "tiles", "inventory", "mission", "last_result", "episode"})
        for tile in observation["tiles"]:
            assert set(tile).issubset({"x", "y", "terrain", "item", "is_exit", "door_open"})
        if message["tick"] == 0:
            assert not any(tile["x"] > 4 or tile.get("item") == "core" for tile in observation["tiles"])
        emit(action_msg(message["request_id"], {"type": "wait"}))

MODES["valid-then-wrong"] = mode_valid_then_wrong
MODES["privacy"] = mode_privacy

def mode_terminal_output(args):
    mode_wait(args)
    sys.stdout.write("unexpected terminal output\n")
    sys.stdout.flush()

def mode_terminal_exit(args):
    mode_wait(args)
    sys.exit(7)

MODES["terminal-output"] = mode_terminal_output
MODES["terminal-exit"] = mode_terminal_exit


def main():
    parser = argparse.ArgumentParser(
        prog="fault_agent",
        description="M3 fault-injection agents for the runtime process session.",
    )
    parser.add_argument("--mode", required=True, help="fault mode to inject")
    parser.add_argument("--marker", default=None,
                        help="exact ready name used by the argv mode")
    parser.add_argument("--seed", type=int, default=0,
                        help="accepted for CLI compatibility (unused)")
    args = parser.parse_args()

    _configure_stdio()

    handler = MODES.get(args.mode)
    if handler is None:
        sys.stderr.write("fault_agent: unknown mode: %s\n" % (args.mode,))
        sys.stderr.flush()
        return 2
    handler(args)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BrokenPipeError:
        try:
            sys.stdout.close()
        finally:
            sys.exit(0)
    except KeyboardInterrupt:
        sys.exit(130)
