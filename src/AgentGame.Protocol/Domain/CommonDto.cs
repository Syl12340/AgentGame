using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>A point in the grid; top-left origin, x right, y down.</summary>
public sealed record PointDto
{
    [JsonPropertyName("x")] public int X { get; init; }
    [JsonPropertyName("y")] public int Y { get; init; }
}

/// <summary>The single one-line action an Agent may send for a decision request ("action" response).</summary>
public sealed record ActionRequestDto
{
    [JsonPropertyName("type")] public ActionTypeDto Type { get; init; }
    [JsonPropertyName("direction")] public DirectionDto? Direction { get; init; }
}

/// <summary>Rule feedback for the last action: status plus an optional stable reason string.</summary>
public sealed record ActionFeedback
{
    [JsonPropertyName("status")] public ActionStatusDto Status { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>Episode termination payload, used by episode_end, observer state and replay footer.</summary>
public sealed record EpisodeBody
{
    [JsonPropertyName("kind")] public EpisodeKindDto Kind { get; init; }
}