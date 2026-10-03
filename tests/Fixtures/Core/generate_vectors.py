"""Independent normative vectors; no C# output or game implementation is consulted."""
import hashlib
import json
import struct
from pathlib import Path

folder = Path(__file__).resolve().parent
scene = json.loads((folder / "facility-small.json").read_text(encoding="utf-8"))

def canonical(*, tick, position, key, door, core, feedback, reason, episode):
    buf = bytearray()
    def i32(value): buf.extend(struct.pack("<i", value))
    def text(value):
        raw = value.encode("utf-8")
        i32(len(raw))
        buf.extend(raw)
    def point(value):
        i32(value["x"])
        i32(value["y"])
    text("core-state/1")
    text("facility-zero/1")
    i32(len(scene["rows"][0]))
    i32(len(scene["rows"]))
    i32(scene["max_ticks"])
    i32(scene["visibility_radius"])
    for field in ("start", "exit", "key", "door", "core"):
        point(scene[field])
    terrain = bytes(1 if cell == "#" else 0 for row in scene["rows"] for cell in row)
    i32(len(terrain))
    buf.extend(terrain)
    buf.extend(struct.pack("<q", tick))
    point(position)
    buf.extend(bytes((key, door, core, feedback)))
    text(reason)
    buf.append(episode)
    return bytes(buf)

initial = canonical(tick=0, position=scene["start"], key=0, door=0, core=0,
                    feedback=0, reason="", episode=0)
success = canonical(tick=13, position=scene["exit"], key=1, door=1, core=1,
                    feedback=1, reason="", episode=1)
first_move = canonical(tick=1, position={"x": 2, "y": 1}, key=0, door=0, core=0, feedback=1, reason="", episode=0)
result = {"encoding": "core-state/1", "source": "generate_vectors.py (independent Python struct encoding)",
          "first_move_sha256": hashlib.sha256(first_move).hexdigest(), "initial_hex": initial.hex(), "initial_sha256": hashlib.sha256(initial).hexdigest(),
          "success_hex": success.hex(), "success_sha256": hashlib.sha256(success).hexdigest()}
(folder / "core-golden.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"bytes": len(initial), "initial_sha256": result["initial_sha256"],
                  "success_sha256": result["success_sha256"]}))