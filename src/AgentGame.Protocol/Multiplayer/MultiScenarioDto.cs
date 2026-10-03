using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>
/// scenario/2: the multi-seat frozen fixture layout. Field set, names and defaults mirror
/// scenario/1 except that <c>start</c> is replaced by a <c>spawns</c> array (a spawn per seat).
/// Top-left origin; x right, y down. rows use ASCII '#' (wall) and '.' (floor) only.
/// Shared objects stay singular: exit, key, door, core. Spawns must be on in-bounds floor and
/// must not overlap the shared key/door/core (they may overlap each other and the exit).
/// </summary>
public sealed record MultiScenarioDto
{
    [JsonPropertyName("format")] public string Format { get; init; } = M2Protocol.ScenarioProtocol;
    [JsonPropertyName("rules")] public string Rules { get; init; } = M2Protocol.RulesVersion;
    [JsonPropertyName("generator")] public string Generator { get; init; } = ProtocolLimits.DefaultGenerator;
    [JsonPropertyName("seed")] public string? Seed { get; init; }
    [JsonPropertyName("generation_attempts")] public int? GenerationAttempts { get; init; }
    [JsonPropertyName("reference_length")] public int? ReferenceLength { get; init; }
    [JsonPropertyName("rows")] public string[] Rows { get; init; } = [];
    [JsonPropertyName("spawns")] public PointDto[] Spawns { get; init; } = [];
    [JsonPropertyName("exit")] public PointDto Exit { get; init; } = new();
    [JsonPropertyName("key")] public PointDto Key { get; init; } = new();
    [JsonPropertyName("door")] public PointDto Door { get; init; } = new();
    [JsonPropertyName("core")] public PointDto Core { get; init; } = new();
    [JsonPropertyName("max_ticks")] public int MaxTicks { get; init; }
    [JsonPropertyName("visibility_radius")] public int VisibilityRadius { get; init; }
}