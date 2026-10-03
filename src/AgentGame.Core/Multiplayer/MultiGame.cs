using System.Collections.Immutable;

namespace AgentGame.Core.Multiplayer;

/// <summary>
/// Multi-seat cooperative rule engine (facility-zero/2). Strict seat rotation: a round is
/// seats 0,1,..N-1 each taking exactly one action; one seat action consumes exactly one tick,
/// so <see cref="MultiScenario.Layout.MaxTicks"/> counts seat-turns shared across all N seats.
/// All seats share one key, one door: any seat picking up the key gives the whole team the key,
/// any holder can open the shared door, doors stay open, and the key is never consumed. The core
/// is carried by exactly ONE seat (the carrier, <see cref="_coreHolder"/>): only the acting seat
/// may pick it up (when nobody holds it yet) and it becomes the sole carrier. A seat's observation
/// reports <see cref="MultiObservation.HasCore"/> only for the carrier, while the key stays shared.
/// The team succeeds only when the carrier itself stands on the exit; a non-carrier teammate
/// standing on the exit does not complete the episode. Otherwise the shared budget truncates it.
/// </summary>
public sealed class MultiGame
{
    private readonly MultiScenario _scenario;
    private readonly Position[] _positions;
    private long _tick;
    private bool _hasKey, _doorOpen;
    private int? _coreHolder;
    private readonly MultiActionResult?[] _lastResults;
    private EpisodeOutcome? _episode;

    private bool HasCore => _coreHolder is not null;

    private MultiGame(MultiScenario scenario)
    {
        _scenario = scenario;
        _positions = scenario.Spawns.ToArray();
        _lastResults = new MultiActionResult?[scenario.SeatCount];
    }

    public static MultiGame Create(MultiScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return new MultiGame(scenario);
    }

    public long Tick => _tick;
    public int OwnerSeat => _episode is null ? (int)(_tick % _scenario.SeatCount) : 0;
    public ImmutableArray<Position> Positions => _positions.ToImmutableArray();
    public Position Position(int seat) => SeatIndex(seat) is int s ? _positions[s] : default;
    public EpisodeOutcome? Episode => _episode;
    public MissionPhase Mission => _episode?.Kind == EpisodeEndKind.Success ? MissionPhase.Succeeded :
        _coreHolder is not null ? MissionPhase.ReturnToExit : _doorOpen ? MissionPhase.FindCore :
        _hasKey ? MissionPhase.OpenDoor : MissionPhase.FindKey;

    public MultiSnapshot Capture()
    {
        var positions = ImmutableArray.CreateBuilder<Position>(_positions.Length);
        foreach (Position p in _positions) positions.Add(p);
        var results = ImmutableArray.CreateBuilder<MultiActionResult?>(_lastResults.Length);
        foreach (MultiActionResult? r in _lastResults) results.Add(r);
        return new(_scenario, _tick, positions.MoveToImmutable(),
            _hasKey, _doorOpen, _coreHolder, results.MoveToImmutable(), _episode);
    }

    /// <summary>Validates a seat index for reads/writes; throws for agents not present.</summary>
    private int SeatIndex(int seat)
    {
        if ((uint)seat >= (uint)_scenario.SeatCount)
            throw new ArgumentOutOfRangeException(nameof(seat), $"Seat {seat} is out of range (0..{_scenario.SeatCount - 1}).");
        return seat;
    }

    /// <summary>Advances one seat-turn for <paramref name="seat"/>. Strict rotation enforced.</summary>
    public MultiStepResult Step(int seat, GameAction action)
    {
        int index = SeatIndex(seat);
        if (_episode is not null)
            throw new InvalidOperationException("Episode has ended.");
        if (index != OwnerSeat)
            throw new InvalidOperationException($"It is not seat {index}'s turn (seat {OwnerSeat} is expected to act).");
        action.Validate();

        MissionPhase oldPhase = Mission;
        var events = ImmutableArray.CreateBuilder<DomainEvent>();
        MultiActionResult outcome;
        Position here = _positions[index];
        switch (action.Kind)
        {
            case ActionKind.Move:
                Position target = here.Adjacent(action.Direction!.Value);
                string? blocked = !_scenario.Layout.Contains(target) ? "out_of_bounds" :
                    _scenario.Layout.At(target) == Terrain.Wall ? "wall" :
                    target == _scenario.Layout.Door && !_doorOpen ? "closed_door" : null;
                if (blocked is not null)
                {
                    outcome = new(ActionStatus.Blocked, blocked);
                    events.Add(new(EventKind.MoveBlocked, here, target, Reason: blocked));
                }
                else
                {
                    events.Add(new(EventKind.Moved, here, target));
                    _positions[index] = target;
                    outcome = new(ActionStatus.Applied);
                }
                break;
            case ActionKind.Pickup:
                if (here == _scenario.Layout.Key && !_hasKey)
                {
                    _hasKey = true;
                    outcome = new(ActionStatus.Applied);
                    events.Add(new(EventKind.PickedUp, To: here, Subject: "key"));
                }
                else if (here == _scenario.Layout.Core && _coreHolder is null)
                {
                    _coreHolder = index;
                    outcome = new(ActionStatus.Applied);
                    events.Add(new(EventKind.PickedUp, To: here, Subject: "core"));
                }
                else
                {
                    outcome = new(ActionStatus.NoEffect, "nothing_to_pick_up");
                    events.Add(new(EventKind.PickupNoEffect, To: here, Reason: outcome.Reason));
                }
                break;
            case ActionKind.Interact:
                Position adjacent = here.Adjacent(action.Direction!.Value);
                if (adjacent != _scenario.Layout.Door)
                    outcome = new(ActionStatus.NoEffect, "no_door");
                else if (_doorOpen)
                    outcome = new(ActionStatus.NoEffect, "door_already_open");
                else if (!_hasKey)
                    outcome = new(ActionStatus.Blocked, "missing_key");
                else
                {
                    _doorOpen = true;
                    outcome = new(ActionStatus.Applied);
                }
                events.Add(outcome.Status == ActionStatus.Applied
                    ? new(EventKind.DoorOpened, To: adjacent, Subject: "door")
                    : new(EventKind.InteractionNoEffect, To: adjacent, Subject: "door", Reason: outcome.Reason));
                break;
            default:
                outcome = new(ActionStatus.Applied);
                events.Add(new(EventKind.Waited, To: here));
                break;
        }
        _tick++;
        if (_coreHolder is int carrier && _positions[carrier] == _scenario.Layout.Exit)
            _episode = new(EpisodeEndKind.Success);
        else if (_tick >= _scenario.Layout.MaxTicks)
            _episode = new(EpisodeEndKind.TurnLimit);
        _lastResults[index] = outcome;
        if (Mission != oldPhase) events.Add(new(EventKind.MissionPhaseChanged, Phase: Mission));
        if (_episode is not null)
            events.Add(new(_episode.Kind == EpisodeEndKind.Success ? EventKind.Succeeded : EventKind.TurnLimitReached));
        return new(index, _tick, outcome, events.ToImmutable(), _episode);
    }

    /// <summary>
    /// Local observation of <paramref name="seat"/> using the single-player visibility rule
    /// (Chebyshev radius + conservative supercover; walls and closed doors block, blockers
    /// themselves are visible) computed from that seat's own position. Because sight is anchored
    /// at the observing seat, the tiles never include a tile only another seat can see, and the
    /// other seats' positions and inventories are never presented as this seat's own.
    /// </summary>
    public MultiObservation Observe(int seat)
    {
        int index = SeatIndex(seat);
        var tiles = ImmutableArray.CreateBuilder<VisibleTile>();
        int r = _scenario.Layout.VisibilityRadius;
        Position origin = _positions[index];
        for (int y = Math.Max(0, origin.Y - r); y <= Math.Min(_scenario.Layout.Height - 1, origin.Y + r); y++)
        for (int x = Math.Max(0, origin.X - r); x <= Math.Min(_scenario.Layout.Width - 1, origin.X + r); x++)
        {
            var p = new Position(x, y);
            if (!Visibility.HasLineOfSight(_scenario.Layout, origin, p, _doorOpen)) continue;
            ItemKind? item = p == _scenario.Layout.Key && !_hasKey ? ItemKind.Key :
                p == _scenario.Layout.Core && _coreHolder is null ? ItemKind.Core : null;
            tiles.Add(new(p, _scenario.Layout.At(p), item, p == _scenario.Layout.Exit, p == _scenario.Layout.Door ? _doorOpen : null));
        }
        return new(_tick, index, origin, tiles.ToImmutable(), _hasKey, _coreHolder == index, Mission, _lastResults[index], _episode);
    }
}