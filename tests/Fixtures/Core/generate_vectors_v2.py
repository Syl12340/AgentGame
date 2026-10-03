"""Independent normative multi-seat vectors for core-state/2 (facility-zero/2).

No C# output or game implementation is consulted. The frozen bytes are produced
directly with Python struct following the little-endian field order documented in
the M7 Core state encoding. The core is carried by a single seat: the field after
has_key/door_open is the carrier seat index (Int32, -1 when nobody holds it).

Three deterministic vectors are emitted for a fixed 2-seat scenario:
  * initial   - nothing picked up, no carrier, episode none
  * carrier   - a real seat has picked up the core (seat 1 is the carrier)
  * success   - the carrier (seat 1) stands on the exit and the episode succeeded
"""
import hashlib
import json
import struct
from pathlib import Path

folder = Path(__file__).resolve().parent

# The fixed 2-seat fixture (must stay in agreement with M7MultiSeatChecks).
WIDTH, HEIGHT = 9, 3
MAX_TICKS, VIS_RADIUS = 512, 3
ROWS = ["#########", "#.......#", "#########"]
SPAWNS = [(1, 1), (7, 1)]
EXIT, KEY, DOOR, CORE = (1, 1), (2, 1), (4, 1), (6, 1)


def canonical(*, tick, seats, has_key, door_open, core_holder,
              last_statuses, reasons, episode):
    buf = bytearray()

    def i32(value):
        buf.extend(struct.pack("<i", value))

    def i64(value):
        buf.extend(struct.pack("<q", value))

    def text(value):
        raw = value.encode("utf-8")
        i32(len(raw))
        buf.extend(raw)

    def byte(value):
        buf.append(value)

    def point(x, y):
        i32(x)
        i32(y)
    text("core-state/2")
    text("facility-zero/2")
    i32(WIDTH); i32(HEIGHT); i32(MAX_TICKS); i32(VIS_RADIUS)
    i32(len(SPAWNS))
    for (x, y) in SPAWNS:
        point(x, y)
    point(*EXIT); point(*KEY); point(*DOOR); point(*CORE)
    terrain = bytes(1 if cell == "#" else 0 for row in ROWS for cell in row)
    i32(len(terrain))
    buf.extend(terrain)
    i64(tick)
    for (x, y) in seats:
        point(x, y)
    byte(int(has_key)); byte(int(door_open)); i32(core_holder)
    for (st, reason) in zip(last_statuses, reasons):
        byte(st)
        text(reason)
    byte(episode)
    return bytes(buf)


def sha(b):
    return hashlib.sha256(b).hexdigest()


initial = canonical(tick=0, seats=SPAWNS, has_key=False, door_open=False,
                    core_holder=-1, last_statuses=[0, 0], reasons=["", ""], episode=0)

# "A few seat actions" on the cooperative route, with seat 1 carrying the core:
#   t0 seat0 move east  (1,1)->(2,1)
#   t1 seat1 move west  (7,1)->(6,1)
#   t2 seat0 pickup key at (2,1)
#   t3 seat1 pickup core at (6,1)   -> seat 1 becomes the carrier
carrier = canonical(tick=4, seats=[(2, 1), (6, 1)], has_key=True, door_open=False,
                    core_holder=1, last_statuses=[1, 1], reasons=["", ""], episode=0)

# Success: the carrier (seat 1) carries the core onto the exit at the end of the
# cooperative route. Key held, door opened, core held by seat 1, episode success.
success = canonical(tick=14, seats=[(2, 1), (1, 1)], has_key=True, door_open=True,
                    core_holder=1, last_statuses=[1, 1], reasons=["", ""], episode=1)

result = {
    "encoding": "core-state/2",
    "rules": "facility-zero/2",
    "source": "generate_vectors_v2.py (independent Python struct encoding)",
    "scenario": {
        "width": WIDTH, "height": HEIGHT, "max_ticks": MAX_TICKS,
        "visibility_radius": VIS_RADIUS, "rows": ROWS, "spawns": SPAWNS,
        "exit": list(EXIT), "key": list(KEY), "door": list(DOOR), "core": list(CORE),
    },
    "carrier": {
        "tick": 4, "seats": [[2, 1], [6, 1]], "has_key": True,
        "door_open": False, "core_holder": 1, "episode": 0,
    },
    "success": {
        "tick": 14, "seats": [[2, 1], [1, 1]], "has_key": True,
        "door_open": True, "core_holder": 1, "episode": 1,
    },
    "initial_hex": initial.hex(),
    "initial_sha256": sha(initial),
    "carrier_hex": carrier.hex(),
    "carrier_sha256": sha(carrier),
    "success_hex": success.hex(),
    "success_sha256": sha(success),
}
(folder / "core-golden-v2.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"bytes": len(initial), "carrier_bytes": len(carrier),
                  "success_bytes": len(success),
                  "initial_sha256": result["initial_sha256"],
                  "carrier_sha256": result["carrier_sha256"],
                  "success_sha256": result["success_sha256"]}))