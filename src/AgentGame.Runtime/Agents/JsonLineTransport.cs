#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Agents;

public sealed class JsonLineException : Exception
{
    public string Code { get; }

    public JsonLineException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public sealed class JsonLineTransport
{
    private static readonly ReadOnlyMemory<byte> _lf = new byte[] { (byte)'\n' };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maxLineBytes;
    private readonly byte[] _readBuffer = new byte[4096];
    private int _readOffset;
    private int _readCount;

    public JsonLineTransport(Stream input, Stream output, int maxLineBytes = ProtocolLimits.MaxLineBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (maxLineBytes < 1 || maxLineBytes > ProtocolLimits.MaxLineBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
        }

        _input = input;
        _output = output;
        _maxLineBytes = maxLineBytes;
    }

    public bool HasBufferedData => _readCount > 0;

    public async Task<byte[]> ReadLineAsync(CancellationToken cancellationToken)
    {
        byte[] line = new byte[_maxLineBytes + 1];
        int length = 0;
        bool readAny = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_readCount == 0)
            {
                _readOffset = 0;
                _readCount = await _input.ReadAsync(_readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_readCount == 0)
                {
                    if (!readAny)
                    {
                        throw new EndOfStreamException();
                    }
                    throw new JsonLineException("incomplete_line", "EOF reached before newline.");
                }
            }

            while (_readCount > 0)
            {
                byte b = _readBuffer[_readOffset];
                _readOffset++;
                _readCount--;
                readAny = true;

                if (b == (byte)'\n')
                {
                    if (length > 0 && line[length - 1] == (byte)'\r') length--;
                    return line.AsSpan(0, length).ToArray();
                }
                
                if (length == line.Length)
                    throw new JsonLineException("line_too_long", "Line exceeded maximum length.");
                line[length++] = b;
                if (length > _maxLineBytes)
                {
                    if (length == _maxLineBytes + 1 && b == (byte)'\r')
                    {
                        // Allowed for now, waiting for LF.
                    }
                    else
                    {
                        throw new JsonLineException("line_too_long", "Line exceeded maximum length.");
                    }
                }
            }
        }
    }

    public async Task WriteLineAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        if (json.Length > _maxLineBytes)
        {
            throw new JsonLineException("host_message_too_large", "Message exceeds maximum length.");
        }

        ReadOnlySpan<byte> span = json.Span;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)'\r' || b == (byte)'\n')
            {
                throw new JsonLineException("host_message_invalid", "Message contains raw CR or LF.");
            }
        }

        await _output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(_lf, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReadTrailingByteAsync(CancellationToken cancellationToken)
    {
        if (HasBufferedData)
        {
            return true;
        }

        _readOffset = 0;
        _readCount = await _input.ReadAsync(_readBuffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return _readCount > 0;
    }
}
