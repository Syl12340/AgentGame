using System.Text;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Replay;

public interface IReplayWriter : IAsyncDisposable
{
    ValueTask AppendAsync(object record, CancellationToken cancellationToken = default);
}

/// <summary>A single-owner JSONL writer. Each append includes LF and flushes before it returns.</summary>
public sealed class ReplayFileWriter : IReplayWriter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Exception? _failure;
    private bool _disposed;

    public ReplayFileWriter(string path)
        : this(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous), false) { }

    public ReplayFileWriter(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Replay stream must be writable.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public async ValueTask AppendAsync(object record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null) throw new InvalidOperationException("Replay writer failed and cannot append another record.", _failure);
            try
            {
                string line = ReplayCodec.Encode(record);
                byte[] bytes = Utf8.GetBytes(line + "\n");
                await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _failure = error;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // A broken injected stream must not hold process cleanup indefinitely.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            throw new TimeoutException("Replay writer cleanup waited for an unfinished append.");
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (!_leaveOpen)
                await _stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
