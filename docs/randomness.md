# Randomness

## Generator

`AgentGame.Core.Pcg32` is a PCG-XSH-RR 64/32 generator (64-bit state, 32-bit output,
xorshift-high with random rotate) ported from the reference implementation at
<https://www.pcg-random.org/>.

State update (unchecked `ulong` arithmetic):

```
state = state * 6364136223846793005 + increment
      increment = (initialSequence << 1) | 1
output (PCG-XSH-RR):
      oldstate = state
      state    = oldstate * 6364136223846793005 + increment
      xorsf    = ((oldstate >> 18) ^ oldstate) >> 27   // lower 32 bits
      rot      = oldstate >> 59                        // 0..31
      result   = rotr32(xorsf, rot)
```

Construction follows the reference `pcg32_srandom_r` initialization and reproduces it
exactly: start with `state = 0`, set `increment = (initialSequence << 1) | 1`, step a
scratch zeroed generator once (`0 * multiplier + increment` advances to the value
`increment`, i.e. `_state = _increment`), fold `state += initialState`, then step once more.

`NextInt(int exclusiveMax)` mirrors the reference `pcg32_boundedrand_r`: it always draws
one or more values via rejection sampling. `threshold = (0u - bound) % bound`; it draws
until the RNG value `r >= threshold`, then returns `r % bound`. There is no `bound == 1`
shortcut, so every call consumes at least one RNG value. `exclusiveMax <= 0` throws
`ArgumentOutOfRangeException`.

## Stream derivation

`AgentGame.Core.NamedRandomStreams.Create(ulong seed, string name)` derives a per-pass
stream. Only the names `map`, `objects`, and `mission` are allowed; anything else throws.

Salting bytes, UTF-8 encoded:

```
"generation-rng/1" 0x00 <name> 0x00 <seed in invariant decimal>
```

SHA-256 of those bytes yields a 32-byte digest. The first 8 bytes, interpreted as a
little-endian `ulong`, become the PCG `initialState`; the next 8 bytes, also little-endian,
become the `initialSequence`.

```
Pcg32 = Create(seed, name)  →  new Pcg32(state = LE64(hash[0..7]),
                                         sequence = LE64(hash[8..15]))
```

SHA-256 is used solely as a deterministic mixing/dedup salting step; it is not used as the
stream itself.

## Verification status

A known PCG reference vector is cited below for `new Pcg32(42, 54)`:

```
A15C02B7 7B47F409 BA1D3330 83D2F293 BFA4784B CBED606E
```

This vector was independently verified in a standalone computation in this session: seeding a
`Pcg32` implementation with `new Pcg32(42, 54)` and stepping six times produced exactly
`A15C02B7 7B47F409 BA1D3330 83D2F293 BFA4784B CBED606E`, matching the reference at
<https://www.pcg-random.org/using-pcg-c-basic.html>.