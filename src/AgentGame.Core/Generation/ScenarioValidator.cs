namespace AgentGame.Core;

public sealed record ScenarioValidationResult(bool IsValid, string? Error, SolveResult? Reference);

public static class ScenarioValidator
{
    public static ScenarioValidationResult Validate(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        HashSet<Position> closed = Reachable(scenario, false);
        if (!closed.Contains(scenario.Key)) return new(false, "key_unreachable_before_door", null);
        if (!closed.Contains(scenario.Exit)) return new(false, "exit_unreachable_before_door", null);
        if (closed.Contains(scenario.Core)) return new(false, "door_not_required", null);
        if (!Reachable(scenario, true).Contains(scenario.Core)) return new(false, "core_unreachable", null);
        SolveResult solution = ScenarioSolver.Solve(scenario);
        return new(solution.Success, solution.Error, solution);
    }

    private static HashSet<Position> Reachable(Scenario scenario, bool doorOpen)
    {
        var visited = new HashSet<Position> { scenario.Start };
        var queue = new Queue<Position>();
        queue.Enqueue(scenario.Start);
        while (queue.TryDequeue(out Position position))
            foreach (Direction direction in Enum.GetValues<Direction>())
            {
                Position next = position.Adjacent(direction);
                if (!scenario.Contains(next) || scenario.At(next) == Terrain.Wall ||
                    (!doorOpen && next == scenario.Door) || !visited.Add(next)) continue;
                queue.Enqueue(next);
            }
        return visited;
    }
}
