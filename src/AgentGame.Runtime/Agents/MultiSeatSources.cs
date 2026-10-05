using AgentGame.Protocol;

namespace AgentGame.Runtime.Agents;

/// <summary>
/// Contract implemented by every decision source for a single seat of a multi-seat run. A source is
/// bound to exactly one logical owner loop: the host calls <see cref="StartAsync"/> once, then
/// <see cref="DecideAsync"/> for each rule transition, <see cref="EndAsync"/> once at episode end, and
/// <see cref="CloseAsync"/> exactly once to release process resources. The run owns the source and
/// therefore also owns its lifetime; a source must not be reused after <see cref="CloseAsync"/>.
/// </summary>
public interface IMultiSeatSource
{
    /// <summary>Start the seat: publish seat ownership and complete any handshake.</summary>
    /// <param name="seat">This source's seat index in <c>[0, seatCount)</c>.</param>
    /// <param name="seatCount">Total seats in the run, in <c>[1, 4]</c>.</param>
    /// <returns>The agent name announced for this seat.</returns>
    Task<string> StartAsync(int seat, int seatCount, CancellationToken cancellationToken);

    /// <summary>Request the legal action for one owned-seat observation.</summary>
    Task<ActionRequestDto> DecideAsync(AgentV2ObservationMessage observation, CancellationToken cancellationToken);

    /// <summary>Deliver the terminal episode-end envelope to the seat when it is reachable.</summary>
    Task EndAsync(AgentV2EpisodeEndMessage message, CancellationToken cancellationToken);

    /// <summary>Release all process resources. Call exactly once; never reuse the source afterwards.</summary>
    Task<MultiSeatCleanup> CloseAsync(bool graceful);
}

/// <summary>Result of closing one multi-seat source; process fields are null for non-process sources.</summary>
public sealed record MultiSeatCleanup(int? ProcessId, int? ExitCode, bool ForcedTermination, AgentFailure? Error, string StderrTail);

/// <summary>
/// An <see cref="IMultiSeatSource"/> backed by a real external Agent process speaking the agent/2 wire
/// (V2 handshake and decision messages). The command argument list is frozen at construction, before
/// any start, and an out-of-range seat is rejected by <see cref="StartAsync"/>. Every V1
/// (agent/1) ProcessAgentSession method is preserved; a separate V1-only session is used by the v1
/// runner, while this source drives the V2 methods added for the multi-seat run.
/// </summary>
public sealed class ProcessMultiSeatSource : IMultiSeatSource
{
    private readonly AgentCommand _command;
    private readonly AgentTimeouts _timeouts;
    private ProcessAgentSession? _session;
    private int _seat = -1;
    private bool _started;
    private bool _closed;

    public ProcessMultiSeatSource(AgentCommand command, AgentTimeouts? timeouts = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Executable) || command.Executable.Contains('\0'))
            throw new ArgumentException("Agent executable is required.", nameof(command));
        ArgumentNullException.ThrowIfNull(command.Arguments);
        // Freeze the caller-owned argument list before the first await or process startup.
        string[] frozen = command.Arguments.ToArray();
        if (frozen.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException("Invalid Agent argument.", nameof(command));
        _command = new AgentCommand(command.Executable, frozen, command.WorkingDirectory);
        _timeouts = timeouts ?? new();
        _timeouts.Validate();
    }

    public async Task<string> StartAsync(int seat, int seatCount, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        ValidateSeat(seat, seatCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
            throw new InvalidOperationException("ProcessMultiSeatSource is already started.");
        _started = true;
        _seat = seat;
        // Publish ownership before awaiting the handshake so timeout cleanup can reach the child.
        // The run owner closes this source once on both handshake success and failure.
        _session = ProcessAgentSession.Start(_command, _timeouts);
        ReadyResponse ready = await _session.HandshakeV2Async(seatCount, cancellationToken);
        return ready.Name;
    }

    public Task<ActionRequestDto> DecideAsync(AgentV2ObservationMessage observation, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Seat != _seat)
            throw new InvalidOperationException("The process source received another seat's observation.");
        return RequireStarted().DecideV2Async(observation, cancellationToken);
    }

    public Task EndAsync(AgentV2EpisodeEndMessage message, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        return RequireStarted().EndV2Async(message, cancellationToken);
    }

    public async Task<MultiSeatCleanup> CloseAsync(bool graceful)
    {
        ThrowIfClosed();
        _closed = true;
        ProcessAgentSession? session = _session;
        _session = null;
        if (session is null)
            return new MultiSeatCleanup(null, null, false, null, "");
        int processId = session.ProcessId;
        ShutdownInfo info = await session.CloseAsync(graceful);
        return new MultiSeatCleanup(processId, info.ExitCode, info.Forced, info.Error, session.StderrTail);
    }

    private ProcessAgentSession RequireStarted() =>
        _session ?? throw new InvalidOperationException("ProcessMultiSeatSource has not started (or already failed to start).");

    private void ThrowIfClosed()
    {
        if (_closed) throw new InvalidOperationException("ProcessMultiSeatSource is already closed.");
    }

    private static void ValidateSeat(int seat, int seatCount)
    {
        if (seatCount is < 1 or > M2Protocol.MaxSeatIndex + 1)
            throw new ArgumentOutOfRangeException(nameof(seatCount), "Seat count must be in [1, 4].");
        if (seat < 0 || seat >= seatCount)
            throw new ArgumentOutOfRangeException(nameof(seat), $"Assigned seat must be in [0, {seatCount}).");
    }
}

/// <summary>
/// An in-process <see cref="IMultiSeatSource"/> whose decisions come from a callback. The source is
/// bound to one seat; it only ever hands that seat's own observation to the callback and rejects a
/// foreign seat's observation. There is no external process, so close never kills anything.
/// </summary>
public sealed class HumanMultiSeatSource : IMultiSeatSource
{
    public const string DefaultName = "human";
    private readonly Func<AgentV2ObservationMessage, CancellationToken, Task<ActionRequestDto>> _decide;
    private readonly string _name;
    private int _seat = -1;
    private bool _started;
    private bool _closed;

    public HumanMultiSeatSource(Func<AgentV2ObservationMessage, CancellationToken, Task<ActionRequestDto>> decide, string name = DefaultName)
    {
        ArgumentNullException.ThrowIfNull(decide);
        _decide = decide;
        _name = name;
    }

    public Task<string> StartAsync(int seat, int seatCount, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        ValidateSeat(seat, seatCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (_started) throw new InvalidOperationException("HumanMultiSeatSource is already started.");
        _seat = seat;
        _started = true;
        return Task.FromResult(_name);
    }

    public async Task<ActionRequestDto> DecideAsync(AgentV2ObservationMessage observation, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        RequireStarted();
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        if (observation.Seat != _seat)
            throw new InvalidOperationException($"Human source at seat {_seat} received an observation for seat {observation.Seat}.");
        return await _decide(observation, cancellationToken);
    }

    public Task EndAsync(AgentV2EpisodeEndMessage message, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        RequireStarted();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<MultiSeatCleanup> CloseAsync(bool graceful)
    {
        ThrowIfClosed();
        _closed = true;
        return Task.FromResult(new MultiSeatCleanup(null, null, false, null, ""));
    }

    private void RequireStarted()
    {
        if (!_started) throw new InvalidOperationException("HumanMultiSeatSource has not started.");
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new InvalidOperationException("HumanMultiSeatSource is already closed.");
    }

    private static void ValidateSeat(int seat, int seatCount)
    {
        if (seatCount is < 1 or > M2Protocol.MaxSeatIndex + 1)
            throw new ArgumentOutOfRangeException(nameof(seatCount), "Seat count must be in [1, 4].");
        if (seat < 0 || seat >= seatCount)
            throw new ArgumentOutOfRangeException(nameof(seat), $"Assigned seat must be in [0, {seatCount}).");
    }
}

/// <summary>An <see cref="IMultiSeatSource"/> that always returns a legal wait action with no process.</summary>
public sealed class WaitMultiSeatSource : IMultiSeatSource
{
    public const string DefaultName = "wait";
    private bool _closed;
    private int _seat = -1;

    public Task<string> StartAsync(int seat, int seatCount, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        ValidateSeat(seat, seatCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (_seat >= 0) throw new InvalidOperationException("WaitMultiSeatSource is already started.");
        _seat = seat;
        return Task.FromResult(DefaultName);
    }

    public Task<ActionRequestDto> DecideAsync(AgentV2ObservationMessage observation, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        if (_seat < 0 || observation.Seat != _seat)
            throw new InvalidOperationException("Wait source requires its own started seat.");
        return Task.FromResult(new ActionRequestDto { Type = ActionTypeDto.Wait });
    }

    public Task EndAsync(AgentV2EpisodeEndMessage message, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_seat < 0) throw new InvalidOperationException("WaitMultiSeatSource has not started.");
        return Task.CompletedTask;
    }

    public Task<MultiSeatCleanup> CloseAsync(bool graceful)
    {
        ThrowIfClosed();
        _closed = true;
        return Task.FromResult(new MultiSeatCleanup(null, null, false, null, ""));
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new InvalidOperationException("WaitMultiSeatSource is already closed.");
    }

    private static void ValidateSeat(int seat, int seatCount)
    {
        if (seatCount is < 1 or > M2Protocol.MaxSeatIndex + 1)
            throw new ArgumentOutOfRangeException(nameof(seatCount), "Seat count must be in [1, 4].");
        if (seat < 0 || seat >= seatCount)
            throw new ArgumentOutOfRangeException(nameof(seat), $"Assigned seat must be in [0, {seatCount}).");
    }
}
