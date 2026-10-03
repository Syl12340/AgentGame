using System.Collections.Immutable;

namespace AgentGame.Core;

public sealed record SolveResult(bool Success, ImmutableArray<GameAction> Actions, int ExpandedStates, string? Error);

/// <summary>Internal oracle: breadth-first search through actual Core transitions.</summary>
public static class ScenarioSolver
{
    private static readonly GameAction[] Actions =
    [
        GameAction.Move(Direction.North), GameAction.Move(Direction.East),
        GameAction.Move(Direction.South), GameAction.Move(Direction.West), GameAction.Pickup(),
        GameAction.Interact(Direction.North), GameAction.Interact(Direction.East),
        GameAction.Interact(Direction.South), GameAction.Interact(Direction.West), GameAction.Wait()
    ];

    private readonly record struct SearchKey(Position Position, bool Key, bool Door, bool Core);
    private sealed record Node(Game State, int Parent, GameAction Action);

    public static SolveResult Solve(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var start = Game.Create(scenario);
        var nodes = new List<Node> { new(start, -1, default) };
        var seen = new HashSet<SearchKey> { KeyOf(start) };
        int expanded = 0;
        for (int head = 0; head < nodes.Count; head++)
        {
            Game current = nodes[head].State;
            expanded++;
            foreach (GameAction action in Actions)
            {
                Game next = current.Copy();
                StepResult step = next.Step(action);
                if (step.Episode?.Kind == EpisodeEndKind.Success)
                {
                    var route = new List<GameAction> { action };
                    for (int index = head; nodes[index].Parent >= 0; index = nodes[index].Parent)
                        route.Add(nodes[index].Action);
                    route.Reverse();
                    return new(true, route.ToImmutableArray(), expanded, null);
                }
                if (step.Episode is not null || !seen.Add(KeyOf(next))) continue;
                nodes.Add(new(next, head, action));
            }
        }
        return new(false, [], expanded, "no_solution_within_turn_limit");
    }

    // BFS reaches each rule state at its earliest tick. Waiting/no-effect steps cannot
    // improve a solution under facility-zero/1; LastResult does not affect transitions.
    private static SearchKey KeyOf(Game game)
    {
        CoreSnapshot state = game.Capture();
        return new(state.Position, state.HasKey, state.DoorOpen, state.HasCore);
    }
}
