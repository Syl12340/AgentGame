using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

// Host → Agent JSONL messages (agent/1). These are produced by the runtime and read by
// external agents; the Agent never parses them in this library, but the DTOs fix the wire
// shape and are used to generate exact fixtures.

public sealed record HelloMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "hello";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = ProtocolLimits.AgentProtocol;
    [JsonPropertyName("actions")] public string[] Actions { get; init; } = ProtocolLimits.HelloActions;
}

/// <summary>One visible tile in an Agent observation. No history is sent to the Agent.</summary>
public sealed record AgentTile
{
    [JsonPropertyName("x")] public int X { get; init; }
    [JsonPropertyName("y")] public int Y { get; init; }
    [JsonPropertyName("terrain")] public TerrainDto Terrain { get; init; }
    [JsonPropertyName("item")] public ItemKindDto? Item { get; init; }
    [JsonPropertyName("is_exit")] public bool IsExit { get; init; }
    [JsonPropertyName("door_open")] public bool? DoorOpen { get; init; }
}

public sealed record AgentObservationBody
{
    [JsonPropertyName("position")] public PointDto Position { get; init; } = new();
    [JsonPropertyName("tiles")] public AgentTile[] Tiles { get; init; } = [];
    [JsonPropertyName("inventory")] public ItemKindDto[] Inventory { get; init; } = [];
    [JsonPropertyName("mission")] public MissionPhaseDto Mission { get; init; }
    [JsonPropertyName("last_result")] public ActionFeedback? LastResult { get; init; }
    [JsonPropertyName("episode")] public EpisodeBody? Episode { get; init; }
}

public sealed record ObservationMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "observation";
    [JsonPropertyName("request_id")] public string RequestId { get; init; } = "";
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("observation")] public AgentObservationBody Observation { get; init; } = new();
}

public sealed record EpisodeEndMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "episode_end";
    [JsonPropertyName("request_id")] public string RequestId { get; init; } = "";
    [JsonPropertyName("result")] public EpisodeBody Result { get; init; } = new();
    [JsonPropertyName("observation")] public AgentObservationBody Observation { get; init; } = new();
}