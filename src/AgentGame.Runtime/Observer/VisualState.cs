using AgentGame.Protocol;

namespace AgentGame.Runtime.Observer;

/// <summary>Transactional Observer reducer. It uses explicit wire patches and never runs rules.</summary>
public sealed class VisualState
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private SnapshotMessage _snapshot;
    private VisualState(SnapshotMessage snapshot) => _snapshot = snapshot;

    public static VisualState FromSnapshot(SnapshotMessage snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var owned = (SnapshotMessage)ObserverCodec.Parse(ObserverCodec.Encode(snapshot));
        ValidateHeader(owned.Protocol, owned.RunId, owned.View, owned.BaseSeq, owned.Tick);
        ValidateState(owned.State, owned.Tick);
        return new(owned with { State = Canonical(owned.State) });
    }

    public SnapshotMessage Snapshot() => (SnapshotMessage)ObserverCodec.Parse(ObserverCodec.Encode(_snapshot));

    public void Apply(object envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope is not StepBatchMessage and not AgentStatusMessage)
            throw new ProtocolException("Reducer accepts only step_batch and agent_status envelopes.");
        object owned = ObserverCodec.Parse(ObserverCodec.Encode(envelope));
        switch (owned)
        {
            case AgentStatusMessage status:
                ValidateNext(status.Protocol, status.RunId, status.View, status.Seq, status.Tick, isStep: false);
                _snapshot = _snapshot with { BaseSeq = status.Seq, AgentStatus = status.AgentStatus };
                break;
            case StepBatchMessage step:
                ValidateNext(step.Protocol, step.RunId, step.View, step.Seq, step.Tick, isStep: true);
                ObserverState state = Reduce(step.Patch, step.Tick);
                _snapshot = _snapshot with
                { BaseSeq = step.Seq, Tick = step.Tick, AgentStatus = step.AgentStatus, State = state };
                break;
            default: throw new ProtocolException("Unexpected Observer envelope.");
        }
    }

    private void ValidateNext(string protocol, string runId, string view, long seq, long tick, bool isStep)
    {
        ValidateHeader(protocol, runId, view, seq, tick);
        if (protocol != _snapshot.Protocol || runId != _snapshot.RunId || view != _snapshot.View)
            throw new ProtocolException("Observer stream identity changed; a new snapshot is required.");
        if (_snapshot.BaseSeq >= MaxSafeInteger || seq != _snapshot.BaseSeq + 1)
            throw new ProtocolException("Observer sequence gap or duplicate; a new snapshot is required.");
        long expectedTick = _snapshot.Tick + (isStep ? 1 : 0);
        if (tick != expectedTick) throw new ProtocolException("Observer envelope tick does not match its kind.");
    }

    private ObserverState Reduce(ObserverPatch patch, long tick)
    {
        // All work targets new dictionaries. A rejection leaves the previous snapshot intact.
        var tiles = _snapshot.State.Tiles.ToDictionary(tile => (tile.X, tile.Y));
        var entities = _snapshot.State.Entities.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
        var upserts = new HashSet<string>(StringComparer.Ordinal);
        var coordinates = new HashSet<(int, int)>();
        foreach (ObserverTile tile in patch.TileUpserts)
        {
            if (!coordinates.Add((tile.X, tile.Y))) throw new ProtocolException("Duplicate patch tile coordinate.");
            tiles[(tile.X, tile.Y)] = tile;
        }
        foreach (ObserverEntity entity in patch.EntityUpserts)
        {
            if (!upserts.Add(entity.Id)) throw new ProtocolException("Duplicate patch entity ID.");
            entities[entity.Id] = entity;
        }
        var removals = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in patch.EntityRemovals)
        {
            if (!removals.Add(id) || upserts.Contains(id)) throw new ProtocolException("Conflicting patch entity ID.");
            if (!entities.Remove(id)) throw new ProtocolException("Cannot remove an unknown entity.");
        }
        var visible = new HashSet<(int, int)>();
        foreach (PointDto point in patch.VisibleTiles)
        {
            if (!visible.Add((point.X, point.Y))) throw new ProtocolException("Duplicate visible coordinate.");
            if (!tiles.TryGetValue((point.X, point.Y), out var tile) || tile.LastSeenTick is not null)
                throw new ProtocolException("Visible coordinate must identify a current tile.");
        }
        foreach (ObserverTile tile in tiles.Values)
            if ((tile.LastSeenTick is null) != visible.Contains((tile.X, tile.Y)))
                throw new ProtocolException("Visible coordinates and tile memory disagree.");
        var next = new ObserverState
        {
            VisibleRadius = _snapshot.State.VisibleRadius,
            Tiles = tiles.Values.ToArray(), Entities = entities.Values.ToArray(),
            Inventory = patch.Inventory, MissionPhase = patch.MissionPhase, Episode = patch.Episode
        };
        ValidateState(next, tick);
        return Canonical(next);
    }

    private static void ValidateHeader(string protocol, string runId, string view, long seq, long tick)
    {
        if (protocol != ProtocolLimits.ObserverProtocol || view != ProtocolLimits.ViewAgent || string.IsNullOrWhiteSpace(runId))
            throw new ProtocolException("Invalid Observer stream identity.");
        if (seq < 0 || seq > MaxSafeInteger || tick < 0 || tick > MaxSafeInteger)
            throw new ProtocolException("Observer sequence and tick must be nonnegative safe integers.");
    }

    private static void ValidateState(ObserverState state, long tick)
    {
        if (state.VisibleRadius is < 0 or > 128) throw new ProtocolException("Invalid visibility radius.");
        var tiles = new HashSet<(int, int)>();
        foreach (ObserverTile tile in state.Tiles)
        {
            if (tile.X < 0 || tile.Y < 0 || !tiles.Add((tile.X, tile.Y)))
                throw new ProtocolException("Invalid or duplicate tile coordinate.");
            if (tile.LastSeenTick is long seen && (seen < 0 || seen > tick))
                throw new ProtocolException("Historical tile timestamp cannot exceed the current tick.");
        }
        var entities = new HashSet<string>(StringComparer.Ordinal);
        foreach (ObserverEntity entity in state.Entities)
        {
            if (string.IsNullOrWhiteSpace(entity.Id) || string.IsNullOrWhiteSpace(entity.Kind) ||
                entity.X < 0 || entity.Y < 0 || !entities.Add(entity.Id))
                throw new ProtocolException("Invalid or duplicate entity.");
            if (!tiles.Contains((entity.X, entity.Y))) throw new ProtocolException("Entity must have a known tile.");
        }
        if (state.Inventory.Distinct().Count() != state.Inventory.Length)
            throw new ProtocolException("Duplicate inventory item.");
    }

    private static ObserverState Canonical(ObserverState state) => state with
    {
        Tiles = state.Tiles.OrderBy(tile => tile.Y).ThenBy(tile => tile.X).ToArray(),
        Entities = state.Entities.OrderBy(entity => entity.Id, StringComparer.Ordinal).ToArray(),
        Inventory = state.Inventory.Order().ToArray()
    };
}
