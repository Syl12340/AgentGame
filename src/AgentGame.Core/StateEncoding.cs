using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentGame.Core;

/// <summary>Versioned, little-endian canonical bytes, deliberately independent of JSON.</summary>
public static class StateEncoding
{
    public const string Version = "core-state/1";
    public static string Hash(CoreSnapshot state) => Convert.ToHexStringLower(SHA256.HashData(Encode(state)));
    public static byte[] Encode(CoreSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var writer = new ArrayBufferWriter<byte>();
        Scenario scene = state.Scenario;
        Text(Version); Text(scene.RulesVersion);
        Int32(scene.Width); Int32(scene.Height); Int32(scene.MaxTicks); Int32(scene.VisibilityRadius);
        Point(scene.Start); Point(scene.Exit); Point(scene.Key); Point(scene.Door); Point(scene.Core);
        Int32(scene.Tiles.Length);
        foreach (Terrain tile in scene.Tiles) Byte((byte)tile);
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), state.Tick); writer.Advance(8);
        Point(state.Position);
        Byte(state.HasKey ? (byte)1 : (byte)0); Byte(state.DoorOpen ? (byte)1 : (byte)0); Byte(state.HasCore ? (byte)1 : (byte)0);
        Byte(state.LastResult is null ? (byte)0 : (byte)((byte)state.LastResult.Status + 1));
        Text(state.LastResult?.Reason ?? "");
        Byte(state.Episode is null ? (byte)0 : state.Episode.Kind == EpisodeEndKind.Success ? (byte)1 : (byte)2);
        return writer.WrittenSpan.ToArray();

        void Byte(byte value) { writer.GetSpan(1)[0] = value; writer.Advance(1); }
        void Int32(int value) { BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value); writer.Advance(4); }
        void Point(Position p) { Int32(p.X); Int32(p.Y); }
        void Text(string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value);
            Int32(data.Length); writer.Write(data);
        }
    }
}