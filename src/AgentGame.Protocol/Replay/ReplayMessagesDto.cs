using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>
/// replay/1 JSONL record types. A replay file preserves the full initial Scenario, the committed
/// canonical actions, the action outcomes and per-step Core hash, and every Observer envelope
/// streamed for the run (all seq values). Playback only needs Protocol data; rule verification
/// is performed separately by the Core-based <c>verify</c> path, not by this library.
/// </summary>
public sealed record ReplayRunHeader
{
    [JsonPropertyName("type")] public string Type { get; init; } = "run_header";
    [JsonPropertyName("replay")] public string Replay { get; init; } = ProtocolLimits.ReplayProtocol;
    [JsonPropertyName("agent_protocol")] public string AgentProtocol { get; init; } = ProtocolLimits.AgentProtocol;
    [JsonPropertyName("observer_protocol")] public string ObserverProtocol { get; init; } = ProtocolLimits.ObserverProtocol;
    [JsonPropertyName("rules")] public string Rules { get; init; } = ProtocolLimits.RulesVersion;
    [JsonPropertyName("core_encoding")] public string CoreEncoding { get; init; } = ProtocolLimits.CoreEncoding;
    [JsonPropertyName("generator")] public string Generator { get; init; } = ProtocolLimits.DefaultGenerator;
    [JsonPropertyName("agent_name")] public string AgentName { get; init; } = "";
    [JsonPropertyName("scenario")] public ScenarioDto Scenario { get; init; } = new();
    [JsonPropertyName("initial_snapshot")] public SnapshotMessage InitialSnapshot { get; init; } = new();
}

/// <summary>
/// Peripheral status envelope in the ordered Observer stream, carrying the full envelope
/// identity (run_id/view/protocol) plus a seq that can advance without a new tick. Because it is
/// part of the same ordered stream as the snapshot, its seq must not collide with the initial
/// snapshot's <c>base_seq</c>: the first post-snapshot envelope is <c>base_seq + 1</c>.
/// </summary>
public sealed record ReplayStatusRecord
{
    [JsonPropertyName("type")] public string Type { get; init; } = "status_record";
    [JsonPropertyName("observer_status")] public AgentStatusMessage ObserverStatus { get; init; } = new();
}

/// <summary>One committed rule step: the canonical action, outcome, Core hash and its Observer batch.</summary>
public sealed record ReplayStepRecord
{
    [JsonPropertyName("type")] public string Type { get; init; } = "step_record";
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("action")] public ActionRequestDto Action { get; init; } = new();
    [JsonPropertyName("outcome")] public ActionFeedback Outcome { get; init; } = new();
    [JsonPropertyName("core_hash")] public string CoreHash { get; init; } = "";
    [JsonPropertyName("view_hash")] public string? ViewHash { get; init; }
    [JsonPropertyName("observer_batch")] public StepBatchMessage ObserverBatch { get; init; } = new();
}

public sealed record ReplayRunFooter
{
    [JsonPropertyName("type")] public string Type { get; init; } = "run_footer";
    [JsonPropertyName("status")] public string Status { get; init; } = "completed";
    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public EpisodeBody? Result { get; init; }
    [JsonPropertyName("last_tick")] public long LastTick { get; init; }
    [JsonPropertyName("stats")] public object? Stats { get; init; }
}