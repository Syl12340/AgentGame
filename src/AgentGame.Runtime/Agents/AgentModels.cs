using AgentGame.Protocol;

namespace AgentGame.Runtime.Agents;

public sealed record AgentCommand(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);

public sealed record AgentTimeouts
{
    public TimeSpan Handshake { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Decision { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(3);

    internal void Validate()
    {
        foreach (TimeSpan value in new[] { Handshake, Decision, ShutdownGrace, KillWait })
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(AgentTimeouts), "Each timeout must be positive and within Int32 milliseconds.");
        if ((ShutdownGrace + KillWait).TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(AgentTimeouts), "Combined shutdown timeout is too large.");
    }
}

public sealed record AgentFailure(string Code, string Phase, string Detail, string? RequestId = null);

internal sealed class AgentSessionException(string code, string phase, string detail, string? requestId = null)
    : Exception(detail)
{
    public AgentFailure Failure { get; } = new(code, phase, detail, requestId);
}

public sealed record RunResult(string Kind, bool Terminated, bool Truncated, long Tick,
    string? AgentName, string? LastRequestId, ActionRequestDto? LastAction, EpisodeBody? Episode,
    AgentFailure? Error, string CoreHash, string StderrTail, int? AgentExitCode, int? AgentProcessId, bool ForcedTermination)
{
    public string Format => "run/1";
}

internal sealed record ShutdownInfo(int? ExitCode, bool Forced, AgentFailure? Error);
