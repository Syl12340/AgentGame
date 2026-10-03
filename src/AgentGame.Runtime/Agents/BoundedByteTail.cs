using System.Text;

namespace AgentGame.Runtime.Agents;

/// <summary>Diagnostic bytes only; invalid stderr UTF8 is replaced, never parsed as a protocol reply.</summary>
internal sealed class BoundedByteTail(int capacity)
{
    private readonly byte[] _bytes = new byte[capacity];
    private readonly object _gate = new();
    private int _offset, _count;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
            foreach (byte value in bytes)
            {
                _bytes[_offset] = value;
                _offset = (_offset + 1) % _bytes.Length;
                _count = Math.Min(_count + 1, _bytes.Length);
            }
    }

    public string Read()
    {
        lock (_gate)
        {
            byte[] result = new byte[_count];
            int start = (_offset - _count + _bytes.Length) % _bytes.Length;
            for (int i = 0; i < result.Length; i++) result[i] = _bytes[(start + i) % _bytes.Length];
            return Encoding.UTF8.GetString(result);
        }
    }
}
