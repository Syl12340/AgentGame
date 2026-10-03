using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentGame.Core.Multiplayer;

/// <summary>
/// Derives the named random streams the multi-seat generator consumes, delegating the exact
/// derivation to the same salted formula as the frozen v1
/// <see cref="AgentGame.Core.NamedRandomStreams"/>. This single public helper is the only place a
/// new stream name can be derived, so the v1 derivation can never silently drift behind a
/// duplicated copy: the three v1 names stay pinned to the v1 API by construction, and the
/// multi-only <c>spawns</c> name is added here that the v1 allow-list does not expose.
/// </summary>
public static class MultiRandomStreams
{
    private static readonly HashSet<string> AllowedNames = new(System.StringComparer.Ordinal)
    {
        "map",
        "objects",
        "mission",
        "spawns",
    };

    /// <summary>
    /// Creates a <see cref="Pcg32"/> stream for the named generation pass. Reuses the exact v1
    /// derivation: SHA-256 of <c>"generation-rng/1"</c> + NUL + <paramref name="name"/> + NUL +
    /// the invariant decimal <paramref name="seed"/>; the first eight hash bytes little-endian form
    /// the PCG state and the next eight form the sequence. Only <c>map</c>, <c>objects</c>,
    /// <c>mission</c> and <c>spawns</c> are allowed, mirroring the v1 rejection style.
    /// </summary>
    public static Pcg32 Create(ulong seed, string name)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        if (!AllowedNames.Contains(name))
        {
            throw new System.ArgumentException($"Unsupported random stream name '{name}'.", nameof(name));
        }

        // Salting material mirrors v1 exactly: "generation-rng/1" + NUL + name + NUL + UInt64
        // invariant decimal seed.
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("generation-rng/1"));
        bytes.Add(0);
        bytes.AddRange(Encoding.UTF8.GetBytes(name));
        bytes.Add(0);
        bytes.AddRange(Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture)));

        byte[] hash = SHA256.HashData(bytes.ToArray());

        // First 8 bytes little-endian form the state; next 8 bytes little-endian form the sequence.
        ulong state = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(0, 8));
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8, 8));
        return new Pcg32(state, sequence);
    }
}