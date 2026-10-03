using System.Collections.Immutable;

namespace AgentGame.Core;

/// <summary>Single-owner rule engine. No I/O, wall clock, observer, or process state.</summary>
public sealed class Game
{
    private readonly Scenario _scenario;
    private Position _position;
    private long _tick;
    private bool _hasKey, _doorOpen, _hasCore;
    private ActionOutcome? _lastResult;
    private EpisodeOutcome? _episode;

    private Game(Scenario scenario) { _scenario = scenario; _position = scenario.Start; }
    public static Game Create(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return new Game(scenario);
    }
    public long Tick => _tick;
    public Position Position => _position;
    public EpisodeOutcome? Episode => _episode;
    public MissionPhase Mission => _episode?.Kind == EpisodeEndKind.Success ? MissionPhase.Succeeded :
        _hasCore ? MissionPhase.ReturnToExit : _doorOpen ? MissionPhase.FindCore :
        _hasKey ? MissionPhase.OpenDoor : MissionPhase.FindKey;
    public CoreSnapshot Capture() => new(_scenario, _tick, _position, _hasKey, _doorOpen, _hasCore, _lastResult, _episode);

    // Search branches share the immutable layout, but own every mutable rule field.
    internal Game Copy() => new(_scenario)
    {
        _position = this._position, _tick = this._tick,
        _hasKey = this._hasKey, _doorOpen = this._doorOpen, _hasCore = this._hasCore,
        _lastResult = this._lastResult, _episode = this._episode
    };

    public StepResult Step(GameAction action)
    {
        if (_episode is not null) throw new InvalidOperationException("Episode has ended.");
        action.Validate();
        MissionPhase oldPhase = Mission;
        var events = ImmutableArray.CreateBuilder<DomainEvent>();
        ActionOutcome outcome;
        switch (action.Kind)
        {
            case ActionKind.Move:
                Position target = _position.Adjacent(action.Direction!.Value);
                string? blocked = !_scenario.Contains(target) ? "out_of_bounds" :
                    _scenario.At(target) == Terrain.Wall ? "wall" :
                    target == _scenario.Door && !_doorOpen ? "closed_door" : null;
                if (blocked is not null)
                {
                    outcome = new(ActionStatus.Blocked, blocked);
                    events.Add(new(EventKind.MoveBlocked, _position, target, Reason: blocked));
                }
                else
                {
                    events.Add(new(EventKind.Moved, _position, target));
                    _position = target;
                    outcome = new(ActionStatus.Applied);
                }
                break;
            case ActionKind.Pickup:
                if (_position == _scenario.Key && !_hasKey)
                {
                    _hasKey = true;
                    outcome = new(ActionStatus.Applied);
                    events.Add(new(EventKind.PickedUp, To: _position, Subject: "key"));
                }
                else if (_position == _scenario.Core && !_hasCore)
                {
                    _hasCore = true;
                    outcome = new(ActionStatus.Applied);
                    events.Add(new(EventKind.PickedUp, To: _position, Subject: "core"));
                }
                else
                {
                    outcome = new(ActionStatus.NoEffect, "nothing_to_pick_up");
                    events.Add(new(EventKind.PickupNoEffect, To: _position, Reason: outcome.Reason));
                }
                break;
            case ActionKind.Interact:
                Position adjacent = _position.Adjacent(action.Direction!.Value);
                if (adjacent != _scenario.Door)
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
            default: // GameAction.Validate guarantees this is Wait.
                outcome = new(ActionStatus.Applied);
                events.Add(new(EventKind.Waited, To: _position));
                break;
        }
        _tick++;
        if (_hasCore && _position == _scenario.Exit)
            _episode = new(EpisodeEndKind.Success);
        else if (_tick >= _scenario.MaxTicks)
            _episode = new(EpisodeEndKind.TurnLimit);
        _lastResult = outcome;
        if (Mission != oldPhase) events.Add(new(EventKind.MissionPhaseChanged, Phase: Mission));
        if (_episode is not null)
            events.Add(new(_episode.Kind == EpisodeEndKind.Success ? EventKind.Succeeded : EventKind.TurnLimitReached));
        return new(_tick, outcome, events.ToImmutable(), _episode);
    }

    public AgentObservation Observe()
    {
        var tiles = ImmutableArray.CreateBuilder<VisibleTile>();
        int r = _scenario.VisibilityRadius;
        for (int y = Math.Max(0, _position.Y - r); y <= Math.Min(_scenario.Height - 1, _position.Y + r); y++)
        for (int x = Math.Max(0, _position.X - r); x <= Math.Min(_scenario.Width - 1, _position.X + r); x++)
        {
            var p = new Position(x, y);
            if (!Visibility.HasLineOfSight(_scenario, _position, p, _doorOpen)) continue;
            ItemKind? item = p == _scenario.Key && !_hasKey ? ItemKind.Key :
                p == _scenario.Core && !_hasCore ? ItemKind.Core : null;
            tiles.Add(new(p, _scenario.At(p), item, p == _scenario.Exit, p == _scenario.Door ? _doorOpen : null));
        }
        return new(_tick, _position, tiles.ToImmutable(), _hasKey, _hasCore, Mission, _lastResult, _episode);
    }
}
