using AgentGame.Core;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;

namespace AgentGame.Runtime.Observer;

/// <summary>
/// Multi-seat (observer/2) display projection. A <c>spectator</c> view renders the full public
/// layout every snapshot/step; an <c>agent:k</c> view renders strictly from that seat's own
/// <see cref="MultiObservation"/> (tiles, items, entities, inventory, mission) and never from the
/// shared layout positions, other-seat positions, <c>CoreHolder</c> or the global door flag.
/// Only the scenario's visibility radius, seat count and tick/identity validation come from the
/// snapshot. The spectator full map is never reused in a seat view.
/// </summary>
internal sealed class MultiObserverProjection
{
    private readonly M2Protocol.M2View _view;
    private readonly MultiScenario _scenario;
    private readonly int _radius;
    private Dictionary<Position, ObserverTile> _tiles = [];
    private Dictionary<string, ObserverEntity> _entities = new(StringComparer.Ordinal);
    private long _tick;
    private ItemKindDto[] _inventory = [];
    private MissionPhaseDto _phase;
    private EpisodeBody? _episode;

    public string View => _view.Text;

    public MultiObserverProjection(MultiSnapshot initial, MultiObservation? observation, string view)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(view);
        _view = M2Protocol.ParseView(view);
        _scenario = initial.Scenario;
        if (_view.IsSpectator)
        {
            if (observation is not null)
                throw new ArgumentException("The spectator view cannot observe a seat.");
            _radius = 128;
            _tick = initial.Tick;
            (var tiles, var entities, var inventory, var phase, var episode) = BuildSpectator(initial);
            SetState(tiles, entities, inventory, phase, episode);
        }
        else
        {
            int seat = _view.Seat!.Value;
            if (seat >= _scenario.SeatCount)
                throw new ArgumentException($"Seat {seat} does not exist in a {_scenario.SeatCount}-seat scenario.");
            if (observation is null)
                throw new ArgumentException("An agent view requires an observation.");
            ValidateObservation(observed: observation, snapshot: initial, seat);
            _radius = _scenario.Layout.VisibilityRadius;
            _tick = observation.Tick;
            (var tiles, var entities, var inventory, var phase, var episode) = BuildAgent(observation);
            SetState(tiles, entities, inventory, phase, episode);
        }
    }

    private MultiObserverProjection(M2Protocol.M2View view, MultiScenario scenario, int radius)
    {
        _view = view;
        _scenario = scenario;
        _radius = radius;
    }

    /// <summary>An owned, deep-cloned copy used to stage a projection without mutating a committed one.</summary>
    public MultiObserverProjection Clone()
    {
        var clone = new MultiObserverProjection(_view, _scenario, _radius)
        {
            _tiles = _tiles.ToDictionary(pair => pair.Key, pair => pair.Value),
            _entities = _entities.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            _tick = _tick,
            _inventory = [.. _inventory],
            _phase = _phase,
            _episode = _episode,
        };
        return clone;
    }

    public ObserverState State => new()
    {
        VisibleRadius = _radius,
        Tiles = SortedTiles(_tiles.Values).Select(tile => tile with { }).ToArray(),
        Entities = _entities.Values.OrderBy(entity => entity.Id, StringComparer.Ordinal).Select(entity => entity with { }).ToArray(),
        Inventory = [.. _inventory],
        MissionPhase = _phase,
        Episode = _episode is null ? null : _episode with { },
    };

    public ObserverPatch Apply(MultiSnapshot snapshot, MultiObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Tick != _tick + 1) throw new ArgumentException("Snapshot tick must advance once.");
        if (!ReferenceEquals(snapshot.Scenario, _scenario))
            throw new ArgumentException("A snapshot must continue the same immutable scenario.");
        Dictionary<Position, ObserverTile> nextTiles;
        Dictionary<string, ObserverEntity> nextEntities;
        ItemKindDto[] inventory;
        MissionPhaseDto phase;
        EpisodeBody? episode;
        if (_view.IsSpectator)
        {
            if (observation is not null)
                throw new ArgumentException("The spectator view cannot observe a seat.");
            (nextTiles, nextEntities, inventory, phase, episode) = BuildSpectator(snapshot);
        }
        else
        {
            if (observation is null)
                throw new ArgumentException("An agent view requires an observation.");
            ValidateObservation(observation, snapshot, _view.Seat!.Value);
            (nextTiles, nextEntities, inventory, phase, episode) = BuildAgent(observation);
        }
        // Diff against the committed cache before replacing it so an invalid observation never dirties state.
        Dictionary<Position, ObserverTile> oldTiles = _tiles;
        Dictionary<string, ObserverEntity> oldEntities = _entities;
        var patch = new ObserverPatch
        {
            TileUpserts = SortedTiles(nextTiles.Where(pair => !oldTiles.TryGetValue(pair.Key, out var old) || old != pair.Value)
                .Select(pair => pair.Value)).Select(tile => tile with { }).ToArray(),
            EntityUpserts = EntityUpserts(nextEntities, oldEntities),
            EntityRemovals = oldEntities.Keys.Where(id => !nextEntities.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray(),
            VisibleTiles = SortedTiles(nextTiles.Values.Where(tile => tile.LastSeenTick is null))
                .Select(tile => new PointDto { X = tile.X, Y = tile.Y }).ToArray(),
            Inventory = [.. inventory],
            MissionPhase = phase,
            Episode = episode is null ? null : episode with { },
        };
        SetState(nextTiles, nextEntities, inventory, phase, episode);
        _tick = snapshot.Tick;
        return patch;
    }

    /// <summary>
    /// Domain events for one step. Spectator: all of them. <c>agent:k</c>: only its own step's
    /// local events plus public phase/episode events (with no position/subject/entity id) from any
    /// seat; never other-seat coordinates, reasons or entity ids.
    /// </summary>
    public ObserverEventDto[] Events(MultiStepResult step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ObserverEventDto[] mapped = ObserverProjection.Events(new StepResult(
            step.Tick, new ActionOutcome(step.Outcome.Status, step.Outcome.Reason), step.Events, step.Episode));
        bool spectator = _view.IsSpectator;
        bool own = spectator || _view.Seat!.Value == step.Seat;
        int actingSeat = step.Seat;
        var result = new List<ObserverEventDto>(mapped.Length);
        foreach (ObserverEventDto item in mapped)
        {
            if (!own)
            {
                // Only global phase/episode events without any seat-specific payload may survive.
                bool global = item.Type is ObserverEventTypeDto.MissionPhaseChanged
                    or ObserverEventTypeDto.Succeeded or ObserverEventTypeDto.TurnLimitReached;
                if (!global || item.Subject is not null || item.EntityId is not null || item.Position is not null)
                    continue;
                result.Add(item with { Reason = null });
                continue;
            }
            // Own or spectator acting-seat events: substitute the acting seat's entity id.
            result.Add(item.EntityId == "player" ? item with { EntityId = $"seat:{actingSeat}" } : item);
        }
        return result.ToArray();
    }

    private (Dictionary<Position, ObserverTile>, Dictionary<string, ObserverEntity>, ItemKindDto[], MissionPhaseDto, EpisodeBody?) BuildAgent(MultiObservation obs)
    {
        var visible = obs.Tiles.ToDictionary(tile => tile.Position);
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
        // A shared-key pickup clears stale remembered key tiles; the core is gone whenever it is not
        // here and the mission/phase proves it was removed (carried by another seat or delivered).
        bool coreRemoved = obs.HasCore || obs.Mission is MissionPhase.ReturnToExit or MissionPhase.Succeeded;
        foreach (var (position, tile) in next.ToArray())
        {
            ItemKindDto? item = tile.Item;
            if ((item == ItemKindDto.Key && obs.HasKey) || (item == ItemKindDto.Core && coreRemoved))
                next[position] = tile with { Item = null };
        }
        string ownId = SeatId(_view.Seat!.Value);
        var entities = new Dictionary<string, ObserverEntity>(StringComparer.Ordinal)
        {
            [ownId] = SeatEntity(ownId, obs.Position.X, obs.Position.Y, obs.HasCore ? "carrying_core" : null)
        };
        foreach (ObserverTile tile in SortedTiles(next.Values))
        {
            if (tile.Item is ItemKindDto.Key) entities["key"] = Entity("key", tile.X, tile.Y);
            if (tile.Item is ItemKindDto.Core) entities["core"] = Entity("core", tile.X, tile.Y);
            if (tile.DoorOpen is bool open) entities["door"] = Entity("door", tile.X, tile.Y, open ? "open" : "closed");
            if (tile.IsExit) entities["exit"] = Entity("exit", tile.X, tile.Y);
        }
        ItemKindDto[] inventory = obs.HasKey ? obs.HasCore ? [ItemKindDto.Key, ItemKindDto.Core] : [ItemKindDto.Key]
            : obs.HasCore ? [ItemKindDto.Core] : [];
        return (next, entities, inventory, Phase(obs.Mission), Episode(obs.Episode));
    }

    private (Dictionary<Position, ObserverTile>, Dictionary<string, ObserverEntity>, ItemKindDto[], MissionPhaseDto, EpisodeBody?) BuildSpectator(MultiSnapshot snapshot)
    {
        var layout = _scenario.Layout;
        var next = new Dictionary<Position, ObserverTile>();
        for (int y = 0; y < layout.Height; y++)
        for (int x = 0; x < layout.Width; x++)
        {
            var p = new Position(x, y);
            next[p] = new()
            {
                X = x, Y = y, Terrain = layout.At(p) == Terrain.Wall ? TerrainDto.Wall : TerrainDto.Floor,
                Item = p == layout.Key && !snapshot.HasKey ? ItemKindDto.Key
                    : p == layout.Core && snapshot.CoreHolder is null ? ItemKindDto.Core : null,
                IsExit = p == layout.Exit,
                DoorOpen = p == layout.Door ? snapshot.DoorOpen : null,
                LastSeenTick = null,
            };
        }
        var entities = new Dictionary<string, ObserverEntity>(StringComparer.Ordinal);
        for (int seat = 0; seat < _scenario.SeatCount; seat++)
        {
            string id = SeatId(seat);
            entities[id] = SeatEntity(id, snapshot.Positions[seat].X, snapshot.Positions[seat].Y,
                snapshot.CoreHolder == seat ? "carrying_core" : null);
        }
        if (!snapshot.HasKey) entities["key"] = Entity("key", layout.Key.X, layout.Key.Y);
        if (snapshot.CoreHolder is null) entities["core"] = Entity("core", layout.Core.X, layout.Core.Y);
        entities["door"] = Entity("door", layout.Door.X, layout.Door.Y, snapshot.DoorOpen ? "open" : "closed");
        entities["exit"] = Entity("exit", layout.Exit.X, layout.Exit.Y);
        var inventory = new List<ItemKindDto>(2);
        if (snapshot.HasKey) inventory.Add(ItemKindDto.Key);
        if (snapshot.CoreHolder is not null) inventory.Add(ItemKindDto.Core);
        MissionPhaseDto phase = snapshot.Episode?.Kind == EpisodeEndKind.Success ? MissionPhaseDto.Succeeded
            : snapshot.CoreHolder is not null ? MissionPhaseDto.ReturnToExit
            : snapshot.DoorOpen ? MissionPhaseDto.FindCore
            : snapshot.HasKey ? MissionPhaseDto.OpenDoor : MissionPhaseDto.FindKey;
        return (next, entities, inventory.ToArray(), phase, Episode(snapshot.Episode));
    }

    private ObserverEntity[] EntityUpserts(Dictionary<string, ObserverEntity> next, Dictionary<string, ObserverEntity> old)
    {
        var result = new List<ObserverEntity>(next.Count);
        foreach (var (id, entity) in next)
        {
            bool always = _view.IsSpectator ? M2Protocol.IsSeatEntityId(id) : id == SeatId(_view.Seat!.Value);
            if (always || !old.TryGetValue(id, out var prev) || prev != entity)
                result.Add(entity with { });
        }
        return result.OrderBy(entity => entity.Id, StringComparer.Ordinal).ToArray();
    }

    private void SetState(Dictionary<Position, ObserverTile> tiles, Dictionary<string, ObserverEntity> entities,
        ItemKindDto[] inventory, MissionPhaseDto phase, EpisodeBody? episode)
    {
        _tiles = tiles;
        _entities = entities;
        _inventory = inventory;
        _phase = phase;
        _episode = episode;
    }

    private void ValidateObservation(MultiObservation observed, MultiSnapshot snapshot, int seat)
    {
        if (observed.Seat != seat || observed.Tick != snapshot.Tick || observed.Position != snapshot.Positions[seat])
            throw new ArgumentException("Observation identity, position or tick disagrees with its snapshot.");
    }

    private static string SeatId(int seat) => $"seat:{seat}";
    private static ObserverEntity SeatEntity(string id, int x, int y, string? state = null) => new()
    { Id = id, Kind = "seat", X = x, Y = y, State = state };
    private static ObserverEntity Entity(string id, int x, int y, string? state = null) => new()
    { Id = id, Kind = id, X = x, Y = y, State = state };
    private static IOrderedEnumerable<ObserverTile> SortedTiles(IEnumerable<ObserverTile> tiles) =>
        tiles.OrderBy(tile => tile.Y).ThenBy(tile => tile.X);
    private static MissionPhaseDto Phase(MissionPhase phase) => phase switch
    {
        MissionPhase.FindKey => MissionPhaseDto.FindKey, MissionPhase.OpenDoor => MissionPhaseDto.OpenDoor,
        MissionPhase.FindCore => MissionPhaseDto.FindCore, MissionPhase.ReturnToExit => MissionPhaseDto.ReturnToExit,
        MissionPhase.Succeeded => MissionPhaseDto.Succeeded, _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };
    private static EpisodeBody? Episode(EpisodeOutcome? outcome) => outcome is null ? null :
        new() { Kind = outcome.Kind == EpisodeEndKind.Success ? EpisodeKindDto.Success : EpisodeKindDto.TurnLimit };
}
