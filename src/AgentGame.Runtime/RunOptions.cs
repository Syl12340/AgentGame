using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

namespace AgentGame.Runtime;

public sealed record RunOptions
{
    public string? RecordPath { get; init; }
    /// <summary>Optional injected writer. The runner owns and disposes it.</summary>
    public IReplayWriter? ReplayWriter { get; init; }
    /// <summary>Called after the initial authority record. Register briefly; consume on another task.</summary>
    public Action<ObserverHub>? ObserverReady { get; init; }
    public string? RunId { get; init; }
}
