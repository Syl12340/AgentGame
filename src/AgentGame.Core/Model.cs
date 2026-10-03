using System.Collections.Immutable;

namespace AgentGame.Core;

public readonly record struct Position(int X, int Y)
{
    public Position Adjacent(Direction direction) => direction switch
    {
        Direction.North => new(X, Y - 1),
        Direction.East => new(X + 1, Y),
        Direction.South => new(X, Y + 1),
        Direction.West => new(X - 1, Y),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}

public enum Direction : byte { North, East, South, West }
public enum Terrain : byte { Floor, Wall }
public enum ItemKind : byte { Key, Core }
public enum ActionKind : byte { Move, Pickup, Interact, Wait }
public enum ActionStatus : byte { Applied, Blocked, NoEffect }
public enum MissionPhase : byte { FindKey, OpenDoor, FindCore, ReturnToExit, Succeeded }
public enum EpisodeEndKind : byte { Success, TurnLimit }
public enum EventKind : byte
{
    Moved, MoveBlocked, PickedUp, DoorOpened, InteractionNoEffect,
    PickupNoEffect, Waited, MissionPhaseChanged, Succeeded, TurnLimitReached
}

public readonly record struct GameAction(ActionKind Kind, Direction? Direction = null)
{
    public static GameAction Move(Direction direction) => new(ActionKind.Move, direction);
    public static GameAction Interact(Direction direction) => new(ActionKind.Interact, direction);
    public static GameAction Pickup() => new(ActionKind.Pickup);
    public static GameAction Wait() => new(ActionKind.Wait);

    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Unknown action kind.");
        bool directional = Kind is ActionKind.Move or ActionKind.Interact;
        if (directional != Direction.HasValue || (Direction.HasValue && !Enum.IsDefined(Direction.Value)))
            throw new ArgumentException("Direction is required exactly for move/interact and must be defined.");
    }
}

public sealed record ActionOutcome(ActionStatus Status, string? Reason = null);
public sealed record EpisodeOutcome(EpisodeEndKind Kind)
{
    public bool Terminated => Kind == EpisodeEndKind.Success;
    public bool Truncated => Kind == EpisodeEndKind.TurnLimit;
}
public sealed record DomainEvent(
    EventKind Kind, Position? From = null, Position? To = null,
    string? Subject = null, string? Reason = null, MissionPhase? Phase = null);
public sealed record StepResult(
    long Tick, ActionOutcome Outcome, ImmutableArray<DomainEvent> Events, EpisodeOutcome? Episode);
public sealed record VisibleTile(
    Position Position, Terrain Terrain, ItemKind? Item, bool IsExit, bool? DoorOpen);
public sealed record AgentObservation(
    long Tick, Position Position, ImmutableArray<VisibleTile> Tiles,
    bool HasKey, bool HasCore, MissionPhase Mission,
    ActionOutcome? LastResult, EpisodeOutcome? Episode);
public sealed record CoreSnapshot(
    Scenario Scenario, long Tick, Position Position,
    bool HasKey, bool DoorOpen, bool HasCore, ActionOutcome? LastResult, EpisodeOutcome? Episode);