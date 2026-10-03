using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentGame.Core;

/// <summary>
/// Derives named, reproducible random streams for map/objects/mission generation from a
/// single seed. Uses only the base library (no JSON, no external I/O, no processes, no
/// wall clock), so the same seed + name always yields the same stream.
/// </summary>
public static class NamedRandomStreams
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        "map",
        "objects",
        "mission",
    };

    /// <summary>
    /// Creates a <see cref="Pcg32"/> stream for the named generation pass. Only
    /// <c>map</c>, <c>objects</c>, and <c>mission</c> are allowed.
    /// </summary>
    public static Pcg32 Create(ulong seed, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!AllowedNames.Contains(name))
        {
            throw new ArgumentException($"Unsupported random stream name '{name}'.", nameof(name));
        }

        // Salting material: "generation-rng/1" + NUL + name + NUL + UInt64 invariant decimal seed.
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