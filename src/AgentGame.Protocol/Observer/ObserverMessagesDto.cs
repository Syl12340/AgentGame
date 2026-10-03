using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>
/// One tile of the viewer's display state. <see cref="LastSeenTick"/> is null for a currently
/// visible tile and set when the tile is only kept in the viewer's display memory (no longer
/// visible). The Agent protocol never carries history; this is observer-only.
/// </summary>
public sealed record ObserverTile
{
    [JsonPropertyName("x")] public int X { get; init; }
    [JsonPropertyName("y")] public int Y { get; init; }
    [JsonPropertyName("terrain")] public TerrainDto Terrain { get; init; }
    [JsonPropertyName("item"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public ItemKindDto? Item { get; init; }
    [JsonPropertyName("is_exit")] public bool IsExit { get; init; }
    [JsonPropertyName("door_open"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool? DoorOpen { get; init; }
    [JsonPropertyName("last_seen_tick"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? LastSeenTick { get; init; }
}

public sealed record ObserverEntity
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("x")] public int X { get; init; }
    [JsonPropertyName("y")] public int Y { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
}

/// <summary>
/// A complete rebuild of the viewer's projectable state for a snapshot reset point. The
/// snapshot state is a full replacement: the viewer replaces its whole state with this.
/// <see cref="ObserverTile.LastSeenTick"/> keeps display-memory history for tiles no longer
/// visible; it is null for currently visible tiles.
/// </summary>
public sealed record ObserverState
{
    [JsonPropertyName("visible_radius")] public int VisibleRadius { get; init; }
    [JsonPropertyName("tiles")] public ObserverTile[] Tiles { get; init; } = [];
    [JsonPropertyName("entities")] public ObserverEntity[] Entities { get; init; } = [];
    [JsonPropertyName("inventory")] public ItemKindDto[] Inventory { get; init; } = [];
    [JsonPropertyName("mission_phase")] public MissionPhaseDto MissionPhase { get; init; }
    [JsonPropertyName("episode"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public EpisodeBody? Episode { get; init; }
}

/// <summary>
/// An explicit incremental patch applied to the snapshot/previous state for one commit step.
/// Unlike the snapshot (full state), a patch describes only what changed: tile upserts,
/// entity upserts/removals, the full current visible-coordinate set, the full inventory,
/// and the complete mission_phase / episode values. Every patch field is required; scalar values
/// replace the previous values. Nullable fields (tile item/door_open/last_seen_tick,
/// patch episode) may be sent as an explicit JSON <c>null</c> to clear
/// the previous value, so the viewer must not reuse stale items.
/// </summary>
public sealed record ObserverPatch
{
    [JsonPropertyName("tile_upserts")] public ObserverTile[] TileUpserts { get; init; } = [];
    [JsonPropertyName("entity_upserts")] public ObserverEntity[] EntityUpserts { get; init; } = [];
    [JsonPropertyName("entity_removals")] public string[] EntityRemovals { get; init; } = [];
    [JsonPropertyName("visible_tiles")] public PointDto[] VisibleTiles { get; init; } = [];
    [JsonPropertyName("inventory")] public ItemKindDto[] Inventory { get; init; } = [];
    [JsonPropertyName("mission_phase")] public MissionPhaseDto MissionPhase { get; init; }
    [JsonPropertyName("episode"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public EpisodeBody? Episode { get; init; }
}

/// <summary>A semantic "what happened" fact; the viewer does not re-derive rules from these.</summary>
public sealed record ObserverEventDto
{
    [JsonPropertyName("type")] public ObserverEventTypeDto Type { get; init; }
    [JsonPropertyName("subject")] public string? Subject { get; init; }
    [JsonPropertyName("entity_id")] public string? EntityId { get; init; }
    [JsonPropertyName("position")] public PointDto? Position { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("phase")] public MissionPhaseDto? Phase { get; init; }
}

public enum ObserverEventTypeDto
{
    Moved, MoveBlocked, PickedUp, DoorOpened, InteractionNoEffect,
    PickupNoEffect, Waited, MissionPhaseChanged, Succeeded, TurnLimitReached,
}

/// <summary>Observer/1 reset point. base_seq is the highest seq already covered, and does NOT
/// consume a broadcast sequence number; the next envelope from this subscription is base_seq+1.</summary>
public sealed record SnapshotMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "snapshot";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = ProtocolLimits.ObserverProtocol;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = ProtocolLimits.ViewAgent;
    [JsonPropertyName("base_seq")] public long BaseSeq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("agent_status")] public AgentStatusDto AgentStatus { get; init; }
    [JsonPropertyName("state")] public ObserverState State { get; init; } = new();
}

/// <summary>Observer/1 incremental envelope for one atomic commit step. Combined in one seq.
/// The carrier is an explicit <see cref="ObserverPatch"/>, not a full state rebuild.</summary>
public sealed record StepBatchMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "step_batch";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = ProtocolLimits.ObserverProtocol;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = ProtocolLimits.ViewAgent;
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("agent_status")] public AgentStatusDto AgentStatus { get; init; }
    [JsonPropertyName("events")] public ObserverEventDto[] Events { get; init; } = [];
    [JsonPropertyName("patch")] public ObserverPatch Patch { get; init; } = new();
}
/// <summary>Standalone ordered peripheral status envelope; does not advance the rule tick.</summary>
public sealed record AgentStatusMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "agent_status";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = ProtocolLimits.ObserverProtocol;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("view")] public string View { get; init; } = ProtocolLimits.ViewAgent;
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("agent_status")] public AgentStatusDto AgentStatus { get; init; }
}
