using System;

namespace AgentGame.Core;

/// <summary>
/// PCG-XSH-RR 64/32 pseudorandom number generator (32-bit output from 64-bit state,
/// xorshift-high with random rotate), ported from the reference implementation at
/// https://www.pcg-random.org/ .
/// </summary>
public sealed class Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _increment;

    /// <summary>
    /// Creates a generator following the reference <c>pcg32_srandom_r</c> initialization:
    /// a zeroed generator is stepped once, the initial state is folded in, then the
    /// generator is stepped once more.
    /// </summary>
    public Pcg32(ulong initialState, ulong initialSequence)
    {
        _state = 0;
        _increment = (initialSequence << 1) | 1UL; // must remain odd

        // Step the scratch zeroed generator once: state 0 advances to the increment value.
        _state = _increment;

        _state = unchecked(_state + initialState);
        Step();
    }

    /// <summary>Returns the next 32-bit value and advances the generator.</summary>
    public uint NextUInt32()
    {
        ulong oldState = _state;
        _state = unchecked(oldState * Multiplier + _increment);

        uint xorshifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
        uint rot = (uint)(oldState >> 59);
        return (xorshifted >> (int)rot) | (xorshifted << (int)((-rot) & 31));
    }

    /// <summary>
    /// Returns a value in <c>[0, exclusiveMax)</c> using rejection sampling so the
    /// produced values have no modulo bias. Throws when <paramref name="exclusiveMax"/>
    /// is not greater than zero.
    /// </summary>
    public int NextInt(int exclusiveMax)
    {
        if (exclusiveMax <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exclusiveMax),
                "exclusiveMax must be greater than zero.");
        }

        uint bound = (uint)exclusiveMax;
        // Reject values below threshold, leaving a whole multiple of bound.
        // Every call consumes at least one RNG value.
        uint threshold = unchecked(0u - bound) % bound;

        uint value;
        do
        {
            value = NextUInt32();
        } while (value < threshold);

        return (int)(value % bound);
    }

    private void Step()
    {
        _state = unchecked(_state * Multiplier + _increment);
    }
}
