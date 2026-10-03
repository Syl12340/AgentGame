#!/usr/bin/env python3
"""agents/random_agent.py -- M3 agent/1 baseline agent.

Reads UTF-8 JSONL from stdin (a ``hello`` handshake followed by
``observation``/``episode_end`` messages) and emits strictly protocol agent/1
JSONL on stdout. It consumes *only* stdin JSONL: it never reads scenario, seed,
Corehash or oracle files. ``--seed <int>`` (default 0) seeds the uniform random
selection among the ten legal actions, giving reproducible sessions for a given
seed.

Protocol conventions (see docs/agent-protocol.md):
  * stdin/stdout are strict UTF-8; stdout carries protocol messages only.
  * errors are reported on stderr and the process exits non-zero.
  * the process stops on ``episode_end`` or on EOF.
"""

import argparse
import json
import random
import sys

HELLO_ACTIONS = ["move", "pickup", "interact", "wait"]
DIRECTIONS = ["north", "east", "south", "west"]


def _make_pool():
    """The ten legal action shapes: 4 moves, 4 interacts, pickup, wait."""
    moves = [{"type": "move", "direction": direction} for direction in DIRECTIONS]
    interacts = [{"type": "interact", "direction": direction} for direction in DIRECTIONS]
    return [*moves, *interacts, {"type": "pickup"}, {"type": "wait"}]


def _emit(obj):
    """Emit one protocol JSON line on stdout and flush."""
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main():
    for stream in (sys.stdin, sys.stdout):
        stream.reconfigure(encoding="utf-8", errors="strict")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(
        prog="random_agent",
        description="M3 random baseline agent over stdin JSONL.",
    )
    parser.add_argument("--seed", type=int, default=0,
                        help="seed for the uniform random action selection (default 0)")
    args = parser.parse_args()

    random.seed(args.seed)
    action_pool = _make_pool()
    started = False

    for line in sys.stdin:
        text = line.rstrip("\r\n")
        if not text.strip():
            continue
        try:
            message = json.loads(text)
        except json.JSONDecodeError:
            sys.stderr.write("random_agent: invalid host JSON\n")
            return 1

        if not isinstance(message, dict):
            sys.stderr.write("random_agent: expected host object\n")
            return 1

        msg_type = message.get("type")
        if not started and msg_type == "hello":
            if message.get("protocol") != "agent/1" or message.get("actions") != HELLO_ACTIONS:
                sys.stderr.write("random_agent: unsupported hello\n")
                return 1
            _emit({"type": "ready", "protocol": "agent/1", "name": "random"})
            started = True
        elif msg_type == "observation":
            if not started:
                sys.stderr.write("random_agent: observation before handshake\n")
                return 1
            request_id = message.get("request_id")
            if not isinstance(request_id, str) or not request_id:
                sys.stderr.write("random_agent: invalid request_id\n")
                return 1
            _emit({
                "type": "action",
                "request_id": request_id,
                "action": random.choice(action_pool),
            })
        elif msg_type == "episode_end":
            break
        else:
            sys.stderr.write("random_agent: unexpected host message\n")
            return 1

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
