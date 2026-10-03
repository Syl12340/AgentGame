"""Independent Python PCG reference arithmetic; no imports of production code."""
import hashlib
import json
from pathlib import Path

MASK64 = (1 << 64) - 1
MASK32 = (1 << 32) - 1

class Pcg:
    def __init__(self, state, sequence):
        self.inc = ((sequence << 1) | 1) & MASK64
        self.state = 0
        self.next()
        self.state = (self.state + state) & MASK64
        self.next()

    def next(self):
        old = self.state
        self.state = (old * 6364136223846793005 + self.inc) & MASK64
        value = (((old >> 18) ^ old) >> 27) & MASK32
        rotation = old >> 59
        return ((value >> rotation) | (value << ((-rotation) & 31))) & MASK32

    def bounded(self, bound):
        threshold = ((-bound) & MASK32) % bound
        while True:
            value = self.next()
            if value >= threshold:
                return value % bound

reference = Pcg(42, 54)
known = [reference.next() for _ in range(6)]
assert known == [0xA15C02B7, 0x7B47F409, 0xBA1D3330, 0x83D2F293, 0xBFA4784B, 0xCBED606E]
streams = []
for seed in (0, 42, MASK64):
    for name in ("map", "objects", "mission"):
        digest = hashlib.sha256(f"generation-rng/1\0{name}\0{seed}".encode("utf-8")).digest()
        random = Pcg(int.from_bytes(digest[:8], "little"), int.from_bytes(digest[8:16], "little"))
        streams.append({"seed": str(seed), "name": name, "values": [random.next() for _ in range(8)]})
limits = [1, 2, 3, 17, 1073741825, 2147483647] * 4
bounded = Pcg(42, 54)
data = {"reference": known, "streams": streams, "bounds": limits,
        "bounded": [bounded.bounded(limit) for limit in limits]}
Path(__file__).with_name("rng-golden.json").write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
print("Independent PCG vectors generated: reference + 9 named streams + 24 bounded draws.")
