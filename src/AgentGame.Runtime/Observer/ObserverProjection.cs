using AgentGame.Core;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Observer;

/// <summary>Agent-view display memory. Only observations reveal tile and entity data.</summary>
internal sealed class ObserverProjection
{
    private readonly int _radius;
    private Dictionary<Position, ObserverTile> _tiles = [];
    private Dictionary<string, ObserverEntity> _entities = new(StringComparer.Ordinal);
    private long _tick;
    private ItemKindDto[] _inventory = [];
    private MissionPhaseDto _phase;
    private EpisodeBody? _episode;

    public ObserverProjection(CoreSnapshot initial, AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(observation);
        if (initial.Tick != observation.Tick || initial.Position != observation.Position)
            throw new ArgumentException("Initial observation does not describe the captured state.");
        _radius = initial.Scenario.VisibilityRadius;
        _tick = observation.Tick;
        Replace(observation);
    }

    public ObserverState State => new()
    {
        VisibleRadius = _radius,
        Tiles = SortedTiles(_tiles.Values).Select(tile => tile with { }).ToArray(),
        Entities = _entities.Values.OrderBy(entity => entity.Id, StringComparer.Ordinal).Select(entity => entity with { }).ToArray(),
        Inventory = [.. _inventory], MissionPhase = _phase, Episode = _episode is null ? null : _episode with { }
    };

    public ObserverPatch Apply(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Tick != _tick + 1) throw new ArgumentException("Observation tick must advance once.");
        Dictionary<Position, ObserverTile> oldTiles = _tiles;
        Dictionary<string, ObserverEntity> oldEntities = _entities;
        Replace(observation);
        return new()
        {
            TileUpserts = SortedTiles(_tiles.Where(pair => !oldTiles.TryGetValue(pair.Key, out var old) || old != pair.Value)
                .Select(pair => pair.Value)).Select(tile => tile with { }).ToArray(),
            EntityUpserts = _entities.Values.Where(entity => entity.Id == "player" ||
                !oldEntities.TryGetValue(entity.Id, out var old) || old != entity)
                .OrderBy(entity => entity.Id, StringComparer.Ordinal).Select(entity => entity with { }).ToArray(),
            EntityRemovals = oldEntities.Keys.Where(id => !_entities.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray(),
            VisibleTiles = SortedTiles(_tiles.Values.Where(tile => tile.LastSeenTick is null))
                .Select(tile => new PointDto { X = tile.X, Y = tile.Y }).ToArray(),
            Inventory = [.. _inventory], MissionPhase = _phase, Episode = _episode is null ? null : _episode with { }
        };
    }

    private void Replace(AgentObservation observation)
    {
        var visible = observation.Tiles.ToDictionary(tile => tile.Position);
        var next = new Dictionary<Position, ObserverTile>(_tiles);
        foreach (var (position, tile) in _tiles)
            if (tile.LastSeenTick is null && !visible.ContainsKey(position))
                next[position] = tile with { LastSeenTick = _tick };
        foreach (var (position, tile) in visible)
            next[position] = new()
            {
                X = position.X, Y = position.Y, Terrain = tile.Terrain == Terrain.Wall ? TerrainDto.Wall : TerrainDto.Floor,
                Item = tile.Item switch { ItemKind.Key => ItemKindDto.Key, ItemKind.Core => ItemKindDto.Core, _ => null },
                IsExit = tile.IsExit, DoorOpen = tile.DoorOpen, LastSeenTick = null
            };
        // Inventory tells us an item was collected even if an older display-memory entry exists.
        foreach (var (position, tile) in next.ToArray())
            if ((tile.Item == ItemKindDto.Key && observation.HasKey) || (tile.Item == ItemKindDto.Core && observation.HasCore))
                next[position] = tile with { Item = null };
        var entities = new Dictionary<string, ObserverEntity>(StringComparer.Ordinal)
        {
            ["player"] = Entity("player", observation.Position.X, observation.Position.Y)
        };
        foreach (ObserverTile tile in SortedTiles(next.Values))
        {
            if (tile.Item is ItemKindDto.Key) entities["key"] = Entity("key", tile.X, tile.Y);
            if (tile.Item is ItemKindDto.Core) entities["core"] = Entity("core", tile.X, tile.Y);
            if (tile.DoorOpen is bool open) entities["door"] = Entity("door", tile.X, tile.Y, open ? "open" : "closed");
            if (tile.IsExit) entities["exit"] = Entity("exit", tile.X, tile.Y);
        }
        _tiles = next; _entities = entities; _tick = observation.Tick;
        _inventory = Inventory(observation); _phase = Phase(observation.Mission);
        _episode = observation.Episode is null ? null : new()
        { Kind = observation.Episode.Kind == EpisodeEndKind.Success ? EpisodeKindDto.Success : EpisodeKindDto.TurnLimit };
    }

    private static ObserverEntity Entity(string id, int x, int y, string? state = null) => new()
    { Id = id, Kind = id, X = x, Y = y, State = state };
    private static IOrderedEnumerable<ObserverTile> SortedTiles(IEnumerable<ObserverTile> tiles) =>
        tiles.OrderBy(tile => tile.Y).ThenBy(tile => tile.X);
    private static ItemKindDto[] Inventory(AgentObservation observation) =>
        observation.HasKey ? observation.HasCore ? [ItemKindDto.Key, ItemKindDto.Core] : [ItemKindDto.Key] :
        observation.HasCore ? [ItemKindDto.Core] : [];
    private static MissionPhaseDto Phase(MissionPhase phase) => phase switch
    {
        MissionPhase.FindKey => MissionPhaseDto.FindKey, MissionPhase.OpenDoor => MissionPhaseDto.OpenDoor,
        MissionPhase.FindCore => MissionPhaseDto.FindCore, MissionPhase.ReturnToExit => MissionPhaseDto.ReturnToExit,
        MissionPhase.Succeeded => MissionPhaseDto.Succeeded, _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    public static ObserverEventDto[] Events(StepResult step) => step.Events.Select(item => new ObserverEventDto
    {
        Type = item.Kind switch
        {
            EventKind.Moved => ObserverEventTypeDto.Moved, EventKind.MoveBlocked => ObserverEventTypeDto.MoveBlocked,
            EventKind.PickedUp => ObserverEventTypeDto.PickedUp, EventKind.DoorOpened => ObserverEventTypeDto.DoorOpened,
            EventKind.InteractionNoEffect => ObserverEventTypeDto.InteractionNoEffect,
            EventKind.PickupNoEffect => ObserverEventTypeDto.PickupNoEffect, EventKind.Waited => ObserverEventTypeDto.Waited,
            EventKind.MissionPhaseChanged => ObserverEventTypeDto.MissionPhaseChanged,
            EventKind.Succeeded => ObserverEventTypeDto.Succeeded, EventKind.TurnLimitReached => ObserverEventTypeDto.TurnLimitReached,
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        },
        Subject = item.Subject,
        EntityId = item.Kind is EventKind.Moved or EventKind.MoveBlocked or EventKind.Waited ? "player" : item.Subject,
        Position = (item.To ?? item.From) is Position position ? new() { X = position.X, Y = position.Y } : null,
        Reason = item.Reason, Phase = item.Phase is MissionPhase phase ? Phase(phase) : null
    }).ToArray();
}
