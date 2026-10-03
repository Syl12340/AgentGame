using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>
/// replay/2 JSONL record types. Mirrors replay/1 but every <c>step_record</c> carries a
/// mandatory <c>seat</c>, and the scenario is a scenario/2 with spawns. Playback only needs
/// Protocol data; rule verification is performed by the Core-based <c>verify</c> path.
/// </summary>
public sealed record MultiReplayRunHeader
{
    [JsonPropertyName("type")] public string Type { get; init; } = "run_header";
    [JsonPropertyName("replay")] public string Replay { get; init; } = M2Protocol.ReplayProtocol;
    [JsonPropertyName("agent_protocol")] public string AgentProtocol { get; init; } = M2Protocol.AgentProtocol;
    [JsonPropertyName("observer_protocol")] public string ObserverProtocol { get; init; } = M2Protocol.ObserverProtocol;
    [JsonPropertyName("rules")] public string Rules { get; init; } = M2Protocol.RulesVersion;
    [JsonPropertyName("core_encoding")] public string CoreEncoding { get; init; } = M2Protocol.CoreEncoding;
    [JsonPropertyName("generator")] public string Generator { get; init; } = ProtocolLimits.DefaultGenerator;
    [JsonPropertyName("agent_name")] public string AgentName { get; init; } = "";
    [JsonPropertyName("scenario")] public MultiScenarioDto Scenario { get; init; } = new();
    [JsonPropertyName("initial_snapshot")] public MultiSnapshotMessage InitialSnapshot { get; init; } = new();
}

public sealed record MultiReplayStatusRecord
{
    [JsonPropertyName("type")] public string Type { get; init; } = "status_record";
    [JsonPropertyName("observer_status")] public MultiAgentStatusMessage ObserverStatus { get; init; } = new();
}

/// <summary>One committed rule step for a seat. <see cref="Seat"/> is mandatory.</summary>
public sealed record MultiReplayStepRecord
{
    [JsonPropertyName("type")] public string Type { get; init; } = "step_record";
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("seat")] public int Seat { get; init; }
    [JsonPropertyName("action")] public ActionRequestDto Action { get; init; } = new();
    [JsonPropertyName("outcome")] public ActionFeedback Outcome { get; init; } = new();
    [JsonPropertyName("core_hash")] public string CoreHash { get; init; } = "";
    [JsonPropertyName("observer_batch")] public MultiStepBatchMessage ObserverBatch { get; init; } = new();
}

public sealed record MultiReplayRunFooter
{
    [JsonPropertyName("type")] public string Type { get; init; } = "run_footer";
    [JsonPropertyName("status")] public string Status { get; init; } = "completed";
    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public EpisodeBody? Result { get; init; }
    [JsonPropertyName("last_tick")] public long LastTick { get; init; }
    [JsonPropertyName("stats")] public object? Stats { get; init; }
}