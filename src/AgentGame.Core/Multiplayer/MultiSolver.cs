using System.Collections.Immutable;

namespace AgentGame.Core.Multiplayer;

/// <summary>
/// A shortest cooperative plan: each element is (seat, action), played in strict seat rotation.
/// <see cref="ExpandedStates"/> is the number of rule states expanded before terminating.
/// </summary>
public sealed record MultiSolveResult(
    bool Success, ImmutableArray<(int Seat, GameAction Action)> Actions,
    int ExpandedStates, string? Error);

/// <summary>
/// Internal oracle for the cooperative facility-zero/2 rules. Breadth-first search that advances
/// ONLY through the real <see cref="MultiGame.Step(int, GameAction)"/> transitions, so a rules
/// change cannot silently diverge from what the tests replay. The dedup key covers every seat's
/// position, the shared key, the door state, the core carrier AND the rotation phase (tick mod N),
/// because the rotation phase decides which seat owns the next turn. BFS reaches each such state at
/// its earliest tick, which also leaves the most shared budget, so the first-visit is optimal.
/// The shared turn budget (<see cref="MultiScenario.Layout.MaxTicks"/>) bounds the depth and a fixed
/// expansion cap bounds the breadth, guaranteeing termination on adversarial layouts.
/// </summary>
public static class MultiSolver
{
    /// <summary>
    /// Fixed deterministic action order applied to whatever seat owns the current turn, mirroring
    /// the v1 single-player oracle so tie-breaking is stable.
    /// </summary>
    private static readonly GameAction[] Actions =
    [
        GameAction.Move(Direction.North), GameAction.Move(Direction.East),
        GameAction.Move(Direction.South), GameAction.Move(Direction.West), GameAction.Pickup(),
        GameAction.Interact(Direction.North), GameAction.Interact(Direction.East),
        GameAction.Interact(Direction.South), GameAction.Interact(Direction.West), GameAction.Wait()
    ];

    /// <summary>
    /// Hard ceiling on expanded states. A finite ceiling is required because a cooperative queuing
    /// game has states that can be revisited (e.g. wasteful wait moves) and the search must still
    /// terminate. The value is large enough that a freshly generated cooperative facility solves in
    /// a single attempt (so generation rarely retries), while still bounding adversarial layouts.
    /// </summary>
    public const int DefaultExpansionCap = 50_000;

    private sealed record BfsNode(int Parent, int Seat, GameAction Action, long Tick);

    public static MultiSolveResult Solve(MultiScenario scenario, int expansionCap = DefaultExpansionCap)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (expansionCap < 1) throw new ArgumentOutOfRangeException(nameof(expansionCap));
        int n = scenario.SeatCount;
        var nodes = new List<BfsNode> { new(-1, -1, default(GameAction), 0) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { KeyOf(scenario, CreateState(scenario), 0) };
        int expanded = 0;

        for (int head = 0; head < nodes.Count; head++)
        {
            BfsNode node = nodes[head];
            if (node.Tick >= scenario.Layout.MaxTicks) continue; // shared budget exhausted -> truncation
            if (expanded >= expansionCap) break;
            expanded++;

            int owner = (int)(node.Tick % n);
            foreach (GameAction action in Actions)
            {
                // Rebuild the exact node state, then advance one seat-turn through the real engine.
                MultiGame child = Rebuild(scenario, nodes, head);
                MultiStepResult step = child.Step(owner, action);
                if (step.Episode?.Kind == EpisodeEndKind.Success)
                    return new(true, BuildPath(nodes, head, owner, action), expanded, null);
                if (step.Episode is not null) continue; // truncated, never a cooperative success
                string key = KeyOf(scenario, child, step.Tick);
                if (!seen.Add(key)) continue;
                nodes.Add(new BfsNode(head, owner, action, step.Tick));
            }
        }
        return new(false, [], expanded, expanded >= expansionCap
            ? "expansion_cap_exceeded"
            : "no_solution_within_turn_limit");
    }

    /// <summary>Replays the root-to-<paramref name="head"/> action path on a fresh game.</summary>
    private static MultiGame Rebuild(MultiScenario scenario, List<BfsNode> nodes, int head)
    {
        var reversed = new List<(int Seat, GameAction Action)>();
        for (int index = head; nodes[index].Parent >= 0; index = nodes[index].Parent)
            reversed.Add((nodes[index].Seat, nodes[index].Action));
        var game = MultiGame.Create(scenario);
        for (int i = reversed.Count - 1; i >= 0; i--)
            game.Step(reversed[i].Seat, reversed[i].Action);
        return game;
    }

    private static ImmutableArray<(int Seat, GameAction Action)> BuildPath(
        List<BfsNode> nodes, int head, int seat, GameAction action)
    {
        var reversed = new List<(int Seat, GameAction Action)> { (seat, action) };
        for (int index = head; nodes[index].Parent >= 0; index = nodes[index].Parent)
            reversed.Add((nodes[index].Seat, nodes[index].Action));
        reversed.Reverse();
        return reversed.ToImmutableArray();
    }

    private static MultiGame CreateState(MultiScenario scenario) => MultiGame.Create(scenario);

    /// <summary>
    /// Content-based dedup key: every seat's coordinate, the shared key, the door state, the core
    /// carrier and the rotation phase. A plain string avoids relying on <see cref="ImmutableArray{T}"/>
    /// struct equality, which compares the wrapped array reference rather than its elements.
    /// </summary>
    private static string KeyOf(MultiScenario scenario, MultiGame game, long tick)
    {
        MultiSnapshot state = game.Capture();
        var sb = new System.Text.StringBuilder(32);
        foreach (Position p in state.Positions) sb.Append(p.X).Append(',').Append(p.Y).Append(';');
        sb.Append(state.HasKey ? 1 : 0).Append(';')
          .Append(state.DoorOpen ? 1 : 0).Append(';')
          .Append(state.CoreHolder ?? -1).Append(';')
          .Append(tick % scenario.SeatCount);
        return sb.ToString();
    }
}