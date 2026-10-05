using AgentGame.Protocol;
using AgentGame.Runtime.Agents;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

namespace AgentGame.Runtime;

public sealed record MultiRunOptions
{
    public string? RecordPath { get; init; }
    /// <summary>The run owns and disposes the injected authority writer.</summary>
    public IReplayWriter? ReplayWriter { get; init; }
    public string RecordView { get; init; } = M2Protocol.ViewSpectator;
    public string? RunId { get; init; }
    /// <summary>Called after recording the header; register briefly and consume asynchronously.</summary>
    public Action<MultiObserverSession>? ObserverReady { get; init; }
}

public sealed record MultiSeatResult(int Seat, string? Name, bool Retired, AgentFailure? Failure,
    MultiSeatCleanup? Cleanup);

public sealed record MultiRunResult(string Kind, bool Terminated, bool Truncated, long Tick,
    string CoreHash, EpisodeBody? Episode, AgentFailure? Error, MultiSeatResult[] Seats)
{
    public string Format => "run/2";
}
