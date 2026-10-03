using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>
/// observer/2 JSONL envelope types. wire shape mirrors observer/1 (snapshot / step_batch /
/// agent_status) but the <c>view</c> is either "spectator" or "agent:&lt;0..3&gt;" and the
/// protocol string is "observer/2". State and patch keep the v1
/// <see cref="ObserverState"/>/<see cref="ObserverPatch"/> shapes.
///
/// Entity-id mapping: a seat's entity is always id <c>"seat:&lt;n&gt;"</c> (n = its seat index,
/// 0..3). A <c>agent:&lt;k&gt;</c> view may carry only <c>seat:k</c> plus shared scene entities
/// (e.g. key / door / exit); referencing any other seat's entity is rejected. The
/// <c>spectator</c> view may carry <c>seat:0</c>..<c>seat:3</c> and all scene entities. A
/// <c>seat:&lt;n&gt;</c> with n &gt; 3 is rejected in every view.
/// </summary>
public sealed record MultiSnapshotMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "snapshot";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = M2Protocol.ObserverProtocol;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = M2Protocol.ViewSpectator;
    [JsonPropertyName("base_seq")] public long BaseSeq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("agent_status")] public AgentStatusDto AgentStatus { get; init; }
    [JsonPropertyName("state")] public ObserverState State { get; init; } = new();
}

public sealed record MultiStepBatchMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "step_batch";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = M2Protocol.ObserverProtocol;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = M2Protocol.ViewSpectator;
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("agent_status")] public AgentStatusDto AgentStatus { get; init; }
    [JsonPropertyName("events")] public ObserverEventDto[] Events { get; init; } = [];
    [JsonPropertyName("patch")] public ObserverPatch Patch { get; init; } = new();
}

public sealed record MultiAgentStatusMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "agent_status";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = M2Protocol.ObserverProtocol;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = M2Protocol.ViewSpectator;
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("agent_status")] public AgentStatusDto AgentStatus { get; init; }
}