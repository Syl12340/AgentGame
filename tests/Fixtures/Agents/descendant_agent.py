#!/usr/bin/env python3
import argparse
import json
import os
import subprocess
import sys
import time

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", required=True)
    parser.add_argument("--pidfile", required=True)
    args = parser.parse_args()

    for stream in (sys.stdin, sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    try:
        child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(180)"])
        pid_info = {"parent": os.getpid(), "child": child.pid}
        
        with open(args.pidfile, "w", encoding="utf-8") as f:
            json.dump(pid_info, f)
            f.flush()
            os.fsync(f.fileno())
    except Exception as e:
        sys.stderr.write(f"Startup error: {e}\n")
        sys.stderr.flush()
        sys.exit(1)

    for line in sys.stdin:
        text = line.rstrip("\r\n")
        if not text.strip():
            continue
        try:
            msg = json.loads(text)
        except json.JSONDecodeError:
            continue
            
        if msg.get("type") == "hello":
            sys.stdout.write(json.dumps({"type": "ready", "protocol": "agent/1", "name": "descendant"}, ensure_ascii=False) + "\n")
            sys.stdout.flush()
            break

    if args.mode == "descendants":
        for line in sys.stdin:
            pass
        while True:
            time.sleep(10)
            
    elif args.mode == "descendants-ignore-shutdown":
        for line in sys.stdin:
            text = line.rstrip("\r\n")
            if not text.strip():
                continue
            try:
                msg = json.loads(text)
            except json.JSONDecodeError:
                continue
            
            if msg.get("type") == "observation":
                resp = {"type": "action", "request_id": msg["request_id"], "action": {"type": "wait"}}
                sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
                sys.stdout.flush()
            elif msg.get("type") == "episode_end":
                break
                
        time.sleep(60)
        sys.exit(0)
    else:
        sys.stderr.write(f"Unknown mode: {args.mode}\n")
        sys.stderr.flush()
        sys.exit(1)

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
