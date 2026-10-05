using AgentGame.Protocol;

namespace AgentGame.Runtime.Replay;

/// <summary>
/// Incremental replay/2 reader retaining only one bounded line and ordering metadata. It mirrors
/// <see cref="ReplayReader"/> but additionally validates every step's mandatory <c>seat</c> against
/// the scenario's seat count and the strict rotation <c>seat == (tick - 1) % seatCount</c>. Playback
/// deliberately does not reject unsupported version strings; rule verification does that separately.
/// </summary>
public sealed class MultiReplayReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _input = new byte[16 * 1024];
    private readonly byte[] _line = new byte[MultiReplayCodec.MaxRecordBytes + 1];
    private int _inputPosition, _inputLength;
    private bool _eof, _footer, _disposed;
    private readonly MultiReplayRunHeader _header;
    private readonly string _runId, _view, _protocol;
    private readonly int _seatCount;
    private EpisodeKindDto? _episode;
    private ReplayFormatException? _failure;

    public MultiReplayReader(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), false) { }

    public MultiReplayReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Replay stream must be readable.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
        try
        {
            object? first = ReadRecord();
            if (first is not MultiReplayRunHeader header) throw Error("missing_header");
            if (header.InitialSnapshot.Tick != 0 || header.InitialSnapshot.BaseSeq != 0)
                throw Error("invalid_initial_boundary");
            _header = header;
            _runId = header.InitialSnapshot.RunId;
            _view = header.InitialSnapshot.View;
            _protocol = header.InitialSnapshot.Protocol;
            _seatCount = header.Scenario.Spawns.Length;
            var parsedView = M2Protocol.ParseView(_view);
            if (!parsedView.IsSpectator && parsedView.Seat >= _seatCount)
                throw Error("view_seat_out_of_range");
            _episode = header.InitialSnapshot.State.Episode?.Kind;
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
    }

    // Never expose the mutable arrays retained for future consistency checks.
    public MultiReplayRunHeader Header => (MultiReplayRunHeader)MultiReplayCodec.Parse(MultiReplayCodec.Encode(_header));
    public string Status { get; private set; } = "incomplete";
    public long LastTick { get; private set; }
    public long LastSeq { get; private set; }
    public int LineNumber { get; private set; }

    public object? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) throw _failure;
        try { return ReadNextCore(); }
        catch (ReplayFormatException error)
        {
            _failure = error;
            throw;
        }
    }

    private object? ReadNextCore()
    {
        object? record = ReadRecord();
        if (record is null) return null;
        if (_footer) throw Error("data_after_footer");
        switch (record)
        {
            case MultiReplayStatusRecord status:
                CheckIdentity(status.ObserverStatus.RunId, status.ObserverStatus.View, status.ObserverStatus.Protocol);
                CheckSequence(status.ObserverStatus.Seq);
                if (status.ObserverStatus.Tick != LastTick) throw Error("status_tick_mismatch");
                LastSeq = status.ObserverStatus.Seq;
                break;
            case MultiReplayStepRecord step:
                CheckIdentity(step.ObserverBatch.RunId, step.ObserverBatch.View, step.ObserverBatch.Protocol);
                CheckSequence(step.Seq);
                if (step.Tick != LastTick + 1) throw Error("step_tick_mismatch");
                CheckSeat(step.Seat, step.Tick);
                if (_episode is not null) throw Error("step_after_episode");
                LastSeq = step.Seq;
                LastTick = step.Tick;
                _episode = step.ObserverBatch.Patch.Episode?.Kind;
                break;
            case MultiReplayRunFooter footer:
                if (footer.LastTick != LastTick) throw Error("footer_tick_mismatch");
                if (footer.Status == "completed" && (_episode is null || footer.Result?.Kind != _episode))
                    throw Error("footer_result_mismatch");
                if (footer.Status == "aborted" && footer.Result is not null && footer.Result.Kind != _episode)
                    throw Error("footer_result_mismatch");
                _footer = true;
                Status = footer.Status;
                break;
            default: throw Error("unexpected_record");
        }
        return record;
    }

    private void CheckSequence(long seq)
    {
        if (seq != LastSeq + 1) throw Error("sequence_gap");
    }

    private void CheckSeat(int seat, long tick)
    {
        if (seat >= _seatCount) throw Error("seat_out_of_range");
        if (seat != (tick - 1) % _seatCount) throw Error("seat_turn_mismatch");
    }

    private void CheckIdentity(string runId, string view, string protocol)
    {
        if (runId != _runId || view != _view || protocol != _protocol) throw Error("observer_identity_mismatch");
    }

    private ReplayFormatException Error(string code) => new(Math.Max(1, LineNumber), code);

    private object? ReadRecord()
    {
        if (_eof) return null;
        int count = 0;
        while (true)
        {
            if (_inputPosition == _inputLength)
            {
                _inputLength = _stream.Read(_input);
                _inputPosition = 0;
                if (_inputLength == 0)
                {
                    _eof = true;
                    if (count > 0)
                    {
                        if (_footer) throw new ReplayFormatException(LineNumber + 1, "data_after_footer");
                        // Unflushed tail is not committed JSONL, even if its JSON happens to be complete.
                        Status = "incomplete";
                    }
                    return null;
                }
            }
            byte next = _input[_inputPosition++];
            if (_footer) throw new ReplayFormatException(LineNumber + 1, "data_after_footer");
            if (next == (byte)'\n')
            {
                LineNumber++;
                if (count > 0 && _line[count - 1] == (byte)'\r') count--;
                if (count > MultiReplayCodec.MaxRecordBytes) throw Error("line_too_long");
                try { return MultiReplayCodec.Parse(_line.AsSpan(0, count)); }
                catch (ProtocolException error) { throw new ReplayFormatException(LineNumber, "invalid_record", error.Message, error); }
            }
            if (count == _line.Length || (count == MultiReplayCodec.MaxRecordBytes && next != (byte)'\r'))
                throw new ReplayFormatException(LineNumber + 1, "line_too_long");
            _line[count++] = next;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}
