using System.Collections.Immutable;

namespace AgentGame.Core.Multiplayer;

/// <summary>
/// Three-layer validation of a cooperative multi-seat scenario, mirroring the v1 validator:
/// (1) structural integrity, (2) cooperative task topology (key reachable before the door, the door
/// genuinely required, core reachable and returnable once open), and (3) full cooperative
/// solvability by <see cref="MultiSolver"/> inside the shared turn budget. A scenario that is
/// solvable but lets the core be reached while the door is still closed is explicitly rejected
/// ("door_not_required") so the locked door is never bypassable.
/// </summary>
public sealed record MultiValidationResult(bool IsValid, string? Error, MultiSolveResult? Reference);

public static class MultiScenarioValidator
{
    public static MultiValidationResult Validate(MultiScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        if (!StructurallyValid(scenario, out string? structuralError))
            return new(false, structuralError, null);

        if (!TryCheckTask(scenario, out string? taskError))
            return new(false, taskError, null);

        // Layer 3: full cooperative solve within the shared turn budget. The solver rejects any
        // cooperative goal it cannot reach, and (via layer 2) any bypass of the locked door.
        MultiSolveResult solution = MultiSolver.Solve(scenario);
        return new(solution.Success, solution.Error, solution);
    }

    private static bool TryCheckTask(MultiScenario scenario, out string? error)
    {
        HashSet<Position> closed = Reachable(scenario, doorOpen: false);
        Scenario s = scenario.Layout;
        if (!closed.Contains(s.Key)) { error = "key_unreachable_before_door"; return false; }
        if (!closed.Contains(s.Exit)) { error = "exit_unreachable_before_door"; return false; }
        if (closed.Contains(s.Core)) { error = "door_not_required"; return false; }
        if (!Reachable(scenario, doorOpen: true).Contains(s.Core)) { error = "core_unreachable"; return false; }
        error = null;
        return true;
    }

    private static bool StructurallyValid(MultiScenario scenario, out string? error)
    {
        int w = scenario.Layout.Width, h = scenario.Layout.Height;
        var rows = new string[h];
        for (int y = 0; y < h; y++)
        {
            char[] line = new char[w];
            for (int x = 0; x < w; x++)
                line[x] = scenario.Layout.At(new(x, y)) == Terrain.Floor ? '.' : '#';
            rows[y] = new string(line);
        }
        try
        {
            _ = new MultiScenario(rows, scenario.Spawns, scenario.Layout.Exit, scenario.Layout.Key,
                scenario.Layout.Door, scenario.Layout.Core,
                scenario.Layout.MaxTicks, scenario.Layout.VisibilityRadius);
        }
        catch (ArgumentOutOfRangeException) { error = "structural_out_of_range"; return false; }
        catch (ArgumentException) { error = "structural_invalid"; return false; }
        error = null;
        return true;
    }

    /// <summary>Tiles reachable from the union of all seat spawns (cooperative stacking).</summary>
    private static HashSet<Position> Reachable(MultiScenario scenario, bool doorOpen)
    {
        var visited = new HashSet<Position>();
        var queue = new Queue<Position>();
        foreach (Position spawn in scenario.Spawns)
            if (visited.Add(spawn)) queue.Enqueue(spawn);
        while (queue.TryDequeue(out Position position))
            foreach (Direction direction in Enum.GetValues<Direction>())
            {
                Position next = position.Adjacent(direction);
                if (!scenario.Layout.Contains(next) || scenario.Layout.At(next) == Terrain.Wall ||
                    (!doorOpen && next == scenario.Layout.Door) || !visited.Add(next)) continue;
                queue.Enqueue(next);
            }
        return visited;
    }
}