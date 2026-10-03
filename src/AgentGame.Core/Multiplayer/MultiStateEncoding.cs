using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentGame.Core.Multiplayer;

/// <summary>
/// Versioned, little-endian canonical bytes for the multi-seat snapshot (core-state/2).
/// Deliberately independent of JSON and object GetHashCode. Field order (seats by index):
/// <list type="number">
/// <item>encoding version "core-state/2"</item>
/// <item>rules version "facility-zero/2"</item>
/// <item>width, height, max_ticks, visibility_radius (Int32 each)</item>
/// <item>seat_count Int32, then each seat's spawn (x,y)</item>
/// <item>exit, key, door, core (x,y)</item>
/// <item>terrain count Int32, then y-major floor=0/wall=1 bytes</item>
/// <item>tick Int64</item>
/// <item>for each seat, its current position (x,y)</item>
/// <item>has_key byte, door_open byte, then the core holder seat index Int32 (-1 when nobody holds it)</item>
/// <item>for each seat: last_result status byte (0 none/1 applied/2 blocked/3 no_effect) and reason string</item>
/// <item>episode byte (0 none, 1 success, 2 turn_limit)</item>
/// </list>
/// Every seat's current position is encoded, so changing any seat - including one that is not
/// currently acting - changes the hash.
/// </summary>
public static class MultiStateEncoding
{
    public const string Version = "core-state/2";
    public static string Hash(MultiSnapshot state) => Convert.ToHexStringLower(SHA256.HashData(Encode(state)));
    public static byte[] Encode(MultiSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var writer = new ArrayBufferWriter<byte>();
        MultiScenario scene = state.Scenario;
        Scenario layout = scene.Layout;
        Text(Version); Text(scene.RulesVersion);
        Int32(layout.Width); Int32(layout.Height); Int32(layout.MaxTicks); Int32(layout.VisibilityRadius);
        Int32(scene.SeatCount);
        foreach (Position spawn in scene.Spawns) Point(spawn);
        Point(layout.Exit); Point(layout.Key); Point(layout.Door); Point(layout.Core);
        Int32(layout.Tiles.Length);
        foreach (Terrain tile in layout.Tiles) Byte((byte)tile);
        Int64(state.Tick);
        foreach (Position p in state.Positions) Point(p);
        Byte(state.HasKey ? (byte)1 : (byte)0); Byte(state.DoorOpen ? (byte)1 : (byte)0); Int32(state.CoreHolder ?? -1);
        for (int i = 0; i < scene.SeatCount; i++)
        {
            MultiActionResult? r = i < state.LastResults.Length ? state.LastResults[i] : null;
            Byte(r is null ? (byte)0 : (byte)((byte)r.Status + 1));
            Text(r?.Reason ?? "");
        }
        Byte(state.Episode is null ? (byte)0 : state.Episode.Kind == EpisodeEndKind.Success ? (byte)1 : (byte)2);
        return writer.WrittenSpan.ToArray();

        void Byte(byte value) { writer.GetSpan(1)[0] = value; writer.Advance(1); }
        void Int32(int value) { BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value); writer.Advance(4); }
        void Int64(long value) { BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), value); writer.Advance(8); }
        void Point(Position p) { Int32(p.X); Int32(p.Y); }
        void Text(string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value);
            Int32(data.Length); writer.Write(data);
        }
    }
}