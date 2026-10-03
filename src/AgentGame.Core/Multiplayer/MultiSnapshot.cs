using System.Collections.Immutable;

namespace AgentGame.Core.Multiplayer;

/// <summary>Immutable per-seat action feedback for a single turn.</summary>
public sealed record MultiActionResult(ActionStatus Status, string? Reason = null);

/// <summary>
/// A single seat-turn result: the seat that acted, the resulting tick, the action outcome,
/// the domain events emitted for this action, and the (possibly) new episode outcome.
/// </summary>
public sealed record MultiStepResult(
    int Seat, long Tick, MultiActionResult Outcome,
    ImmutableArray<DomainEvent> Events, EpisodeOutcome? Episode);

/// <summary>Immutable observation of one seat, never containing tiles only another seat sees.</summary>
public sealed record MultiObservation(
    long Tick, int Seat, Position Position,
    ImmutableArray<VisibleTile> Tiles, bool HasKey, bool HasCore,
    MissionPhase Mission, MultiActionResult? LastResult, EpisodeOutcome? Episode);

/// <summary>
/// Immutable snapshot covering every seat plus the shared rule state. Captures own a reference to
/// the immutable scenario; later steps of the owning game never mutate an earlier snapshot.
/// <para>
/// The core is carried by exactly one seat at a time: <see cref="CoreHolder"/> is the carrying
/// seat index, or null when nobody holds it. The key stays team-shared (<see cref="HasKey"/>),
/// so any seat may pick up the key and any holder may open the shared door.
/// </para>
/// </summary>
public sealed record MultiSnapshot(
    MultiScenario Scenario, long Tick, ImmutableArray<Position> Positions,
    bool HasKey, bool DoorOpen, int? CoreHolder,
    ImmutableArray<MultiActionResult?> LastResults, EpisodeOutcome? Episode);