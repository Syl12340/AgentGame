using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>
/// scenario/1: the frozen fixture layout. Field set, names and defaults match
/// tests/Fixtures/Core/facility-small.json. Top-left origin; x right, y down.
/// rows use ASCII '#' (wall) and '.' (floor) only.
/// </summary>
public sealed record ScenarioDto
{
    [JsonPropertyName("format")] public string Format { get; init; } = ProtocolLimits.ScenarioProtocol;
    [JsonPropertyName("rules")] public string Rules { get; init; } = ProtocolLimits.RulesVersion;
    [JsonPropertyName("generator")] public string Generator { get; init; } = ProtocolLimits.DefaultGenerator;
    [JsonPropertyName("seed")] public string? Seed { get; init; }
    [JsonPropertyName("generation_attempts")] public int? GenerationAttempts { get; init; }
    [JsonPropertyName("reference_length")] public int? ReferenceLength { get; init; }
    [JsonPropertyName("rows")] public string[] Rows { get; init; } = [];
    [JsonPropertyName("start")] public PointDto Start { get; init; } = new();
    [JsonPropertyName("exit")] public PointDto Exit { get; init; } = new();
    [JsonPropertyName("key")] public PointDto Key { get; init; } = new();
    [JsonPropertyName("door")] public PointDto Door { get; init; } = new();
    [JsonPropertyName("core")] public PointDto Core { get; init; } = new();
    [JsonPropertyName("max_ticks")] public int MaxTicks { get; init; }
    [JsonPropertyName("visibility_radius")] public int VisibilityRadius { get; init; }
}