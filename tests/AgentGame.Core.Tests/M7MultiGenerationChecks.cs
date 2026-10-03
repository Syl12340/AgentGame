using System.Collections.Immutable;
using System.Text.Json;
using AgentGame.Core;
using AgentGame.Core.Multiplayer;

namespace AgentGame.Core.Tests;

/// <summary>
/// M7 cooperative <b>generation</b> contract checks for the multi-seat facility-zero/2 rules:
/// real generation, real cooperative solving, real validation, and actual replay through MultiGame.
/// Console style, zero external dependencies, matching M7MultiSeatChecks.
/// </summary>
public static class M7MultiGenerationChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Seeds 0..19 generate 2-seat scenarios that validate and who solver replayed through MultiGame", SeedsGenerateAndReplayAsync),
            ("Generation is deterministic: same seed twice gives the same scenario and initial hash", DeterministicAsync),
            ("A key-behind-door layout and a bypassable-door layout are rejected by the validator", BrokenLayoutsRejectedAsync),
            ("Every solver result replays successfully through MultiGame (never a false plan)", SolverReplayNeverFailsAsync),
            ("The initial hash changes when only one seat spawn changes", SpawnChangeChangesHashAsync),
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M7 Multi-generation: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static Task SeedsGenerateAndReplayAsync()
    {
        int maxLength = 0, maxAttempts = 0;
        for (ulong seed = 0; seed < 20; seed++)
        {
            MultiGenerationResult result = MultiScenarioGenerator.Generate(seed);
            Check(result.Success, $"seed {seed}: {result.Error}");
            Check(result.Attempts <= 16, $"seed {seed}: attempts exceeded bound ({result.Attempts}).");
            MultiValidationResult validation = MultiScenarioValidator.Validate(result.Scenario!);
            Check(validation.IsValid, $"seed {seed}: validated scenario rejected: {validation.Error}");
            int length = result.Reference!.Actions.Length;
            maxLength = Math.Max(maxLength, length); maxAttempts = Math.Max(maxAttempts, result.Attempts);
            // Replay the actual solver plan through MultiGame and require real success. Blocked/no-effect
            // no-ops are legal steps that pass the strict seat rotation, so only out-of-turn or early/late
            // terminal steps are failures.
            var game = MultiGame.Create(result.Scenario!);
            foreach ((int seat, GameAction action) in result.Reference.Actions)
            {
                Equal(seat, game.OwnerSeat);
                game.Step(seat, action);
                if (game.Episode is not null && game.Episode.Kind != EpisodeEndKind.Success)
                    throw new Exception($"seed {seed}: replayed plan was truncated before the goal.");
            }
            Equal(EpisodeEndKind.Success, game.Episode!.Kind, "seed " + seed + ": replayed plan did not succeed.");
        }
        Console.WriteLine($"  Seeds 0..19: 20/20, max plan length {maxLength}, max generation attempts {maxAttempts}.");
        return Task.CompletedTask;
    }

    private static Task DeterministicAsync()
    {
        foreach (ulong seed in new[] { 0UL, 7UL, 42UL, ulong.MaxValue })
        {
            var first = MultiScenarioGenerator.Generate(seed);
            var second = MultiScenarioGenerator.Generate(seed);
            Check(first.Success && second.Success, $"seed {seed}: generation failed.");
            Equal(MultiStateEncoding.Hash(MultiGame.Create(first.Scenario!).Capture()),
                MultiStateEncoding.Hash(MultiGame.Create(second.Scenario!).Capture()),
                "seed " + seed + ": initial hashes differ across identical replays.");
            Check(first.Scenario!.Spawns.SequenceEqual(second.Scenario!.Spawns), "seed " + seed + ": spawns differ.");
            Equal(first.Attempts, second.Attempts, "seed " + seed + ": attempts differ.");
            Check(first.Reference!.Actions.SequenceEqual(second.Reference!.Actions), "seed " + seed + ": solver plans differ.");
        }
        return Task.CompletedTask;
    }

    private static Task BrokenLayoutsRejectedAsync()
    {
        // Key laid out behind the locked door: unreachable until the door is open. Both spawns on
        // the pre-door side so the door is genuinely required and the key-only defect is isolated.
        var keyBehindDoor = new MultiScenario(
            ["#########", "#.......#", "#########"],
            [new(1, 1), new(3, 1)], new(1, 1), new(6, 1), new(4, 1), new(5, 1));
        Equal("key_unreachable_before_door", MultiScenarioValidator.Validate(keyBehindDoor).Error,
            "Key behind the door was not rejected.");

        // Door can be bypassed: the core sits in the open pre-door region with the door closed.
        var bypass = new MultiScenario(
            ["#########", "#.......#", "#########"],
            [new(1, 1), new(7, 1)], new(1, 1), new(2, 1), new(4, 1), new(3, 1));
        Equal("door_not_required", MultiScenarioValidator.Validate(bypass).Error,
            "Bypassable door was not rejected.");
        return Task.CompletedTask;
    }

    private static Task SolverReplayNeverFailsAsync()
    {
        // Hand-built cooperative layouts plus a few generated ones: every solver result must replay.
        var layouts = new[]
        {
            new MultiScenario(["#########", "#.......#", "#########"],
                [new(1, 1), new(7, 1)], new(1, 1), new(2, 1), new(4, 1), new(6, 1)),
            new MultiScenario(["#########", "#.......#", "#########"],
                [new(3, 1), new(7, 1)], new(3, 1), new(1, 1), new(4, 1), new(6, 1)),
        };
        var withGenerated = layouts.ToList();
        for (ulong seed = 0; seed < 6; seed++)
        {
            var r = MultiScenarioGenerator.Generate(seed);
            if (r.Success) withGenerated.Add(r.Scenario!);
        }
        foreach (MultiScenario scenario in withGenerated)
        {
            MultiSolveResult solution = MultiSolver.Solve(scenario);
            Check(solution.Success, $"Solver failed on a cooperative layout: {solution.Error}");
            var game = MultiGame.Create(scenario);
            foreach ((int seat, GameAction action) in solution.Actions)
            {
                Equal(seat, game.OwnerSeat);
                game.Step(seat, action);
            }
            Equal(EpisodeEndKind.Success, game.Episode!.Kind, "Solver returned a plan that does not succeed on replay.");
        }
        return Task.CompletedTask;
    }

    private static Task SpawnChangeChangesHashAsync()
    {
        var first = MultiScenarioGenerator.Generate(0);
        Check(first.Success, "seed 0 generation failed.");
        MultiScenario scenario = first.Scenario!;
        string original = MultiStateEncoding.Hash(MultiGame.Create(scenario).Capture());

        // Pick any alternate in-bounds floor tile that is off key/door/core and not another spawn,
        // then rebuild the scenario with only seat-1's spawn moved.
        Position alternative = FindAlternateSpawn(scenario, originalSpawnIndex: 1);
        Position[] spawns = new Position[scenario.SeatCount];
        scenario.Spawns.CopyTo(spawns, 0);
        spawns[1] = alternative;
        var variant = new MultiScenario(Rows(scenario), spawns, scenario.Layout.Exit, scenario.Layout.Key,
            scenario.Layout.Door, scenario.Layout.Core, scenario.Layout.MaxTicks, scenario.Layout.VisibilityRadius);
        string changed = MultiStateEncoding.Hash(MultiGame.Create(variant).Capture());
        Check(original != changed, "Moving only one seat spawn did not change the initial hash.");
        return Task.CompletedTask;
    }

    private static Position FindAlternateSpawn(MultiScenario scenario, int originalSpawnIndex)
    {
        Position original = scenario.Spawns[originalSpawnIndex];
        int w = scenario.Layout.Width, h = scenario.Layout.Height;
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            var p = new Position(x, y);
            if (p == original) continue;
            if (scenario.Layout.At(p) != Terrain.Floor) continue;
            if (p == scenario.Layout.Key || p == scenario.Layout.Door || p == scenario.Layout.Core) continue;
            if (scenario.Spawns.Contains(p)) continue;
            return p;
        }
        throw new Exception("No alternate spawn tile found.");
    }

    private static string[] Rows(MultiScenario scenario)
    {
        int w = scenario.Layout.Width, h = scenario.Layout.Height;
        var rows = new string[h];
        for (int y = 0; y < h; y++)
        {
            var line = new char[w];
            for (int x = 0; x < w; x++) line[x] = scenario.Layout.At(new(x, y)) == Terrain.Floor ? '.' : '#';
            rows[y] = new string(line);
        }
        return rows;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(message is null ? $"Expected {expected}, got {actual}." : $"{message} (expected {expected}, got {actual}).");
    }
}