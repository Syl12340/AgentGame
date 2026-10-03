using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>The "limits" block in an agent/2 hello: seat count advertised by the run.</summary>
public sealed record AgentV2Limits
{
    [JsonPropertyName("seat_count")] public int SeatCount { get; init; }
}

/// <summary>Host → Agent hello in agent/2. Adds a limits block (seat count) over agent/1.</summary>
public sealed record AgentV2HelloMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "hello";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = M2Protocol.AgentProtocol;
    [JsonPropertyName("actions")] public string[] Actions { get; init; } = M2Protocol.HelloActions;
    [JsonPropertyName("limits")] public AgentV2Limits Limits { get; init; } = new();
}

/// <summary>Host → Agent observation in agent/2. The body reuses the frozen agent/1
/// <see cref="AgentObservationBody"/> shape; the seat field identifies the observing seat.</summary>
public sealed record AgentV2ObservationMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "observation";
    [JsonPropertyName("request_id")] public string RequestId { get; init; } = "";
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("seat")] public int Seat { get; init; }
    [JsonPropertyName("observation")] public AgentObservationBody Observation { get; init; } = new();
}

/// <summary>Host → Agent episode_end in agent/2.</summary>
public sealed record AgentV2EpisodeEndMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "episode_end";
    [JsonPropertyName("request_id")] public string RequestId { get; init; } = "";
    [JsonPropertyName("result")] public EpisodeBody Result { get; init; } = new();
    [JsonPropertyName("observation")] public AgentObservationBody Observation { get; init; } = new();
}