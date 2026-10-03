using System.Collections.Immutable;
using AgentGame.Core;
using AgentGame.Core.Multiplayer;

namespace AgentGame.Core.Tests;

/// <summary>
/// M7 multi-seat edge contract checks for facility-zero/2 rules.
/// Validates rotation cycles across 1..4 seats, blocked moves, shared key mechanics,
/// carrier discipline and cooperative exit requirements, observation isolation,
/// snapshot immutability, and spawn layout validation.
/// </summary>
internal static class M7MultiSeatEdgeChecks
{
    public static async Task<int> RunAsync(string root)
    {
        _ = root;
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Seat rotation across 1, 2, 3 and 4 seats cycles strictly over 2N ticks", SeatRotationAsync),
            ("Wall, closed door and out-of-bounds moves consume turn and report blocked reasons", BlockedMovesAsync),
            ("Shared key allows teammate to open door without visiting key tile", SharedKeyPathAsync),
            ("Carrier discipline enforces single carrier and no victory away from exit", CarrierDisciplineAsync),
            ("Cooperative success requires carrier on exit while non-carrier on exit does not win", CooperativeWinAsync),
            ("Per-seat observations provide isolated tile sets and HasCore reflects only carrier", PerSeatObservationsAsync),
            ("Snapshot and hash remain stable across steps and snapshot collections reject mutation", SnapshotImmutabilityAndHashAsync),
            ("Spawn validation rejects invalid spawns and accepts valid two-seat scenario", SpawnValidationRejectionsAsync),
        };

        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try
            {
                await check();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {error.Message}");
            }
        }
        Console.WriteLine($"M7 Multi-seat edge: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static Task SeatRotationAsync()
    {
        string[] rows = ["##########", "#........#", "#........#", "##########"];
        for (int n = 1; n <= 4; n++)
        {
            var spawns = Enumerable.Range(1, n).Select(x => new Position(x, 2)).ToArray();
            var scene = new MultiScenario(rows, spawns, new(1, 1), new(2, 1), new(3, 1), new(4, 1));
            var game = MultiGame.Create(scene);
            Equal(0L, game.Tick);

            for (int step = 0; step < 2 * n; step++)
            {
                int expectedOwner = step % n;
                Equal((long)step, game.Tick);
                Equal(expectedOwner, game.OwnerSeat);
                var result = game.Step(expectedOwner, GameAction.Wait());
                Equal(expectedOwner, result.Seat);
                Equal((long)(step + 1), result.Tick);
                Equal((long)(step + 1), game.Tick);
            }

            Equal((long)(2 * n), game.Tick);
        }
        return Task.CompletedTask;
    }

    private static Task BlockedMovesAsync()
    {
        string[] rows = [".....", "#...."];
        Position exit = new(0, 0);
        Position key = new(1, 0);
        Position door = new(3, 0);
        Position core = new(4, 0);
        var scene = new MultiScenario(rows, [new(2, 0), new(0, 0)], exit, key, door, core);
        var game = MultiGame.Create(scene);

        // Turn 0 (Seat 0 at (2,0)): move North -> (2,-1) is out of bounds
        var oobResult = game.Step(0, GameAction.Move(Direction.North));
        Equal(ActionStatus.Blocked, oobResult.Outcome.Status);
        Equal("out_of_bounds", oobResult.Outcome.Reason);
        Equal(new Position(2, 0), game.Position(0));
        Equal(1L, game.Tick);
        Equal(1, game.OwnerSeat);
        Equal(EventKind.MoveBlocked, oobResult.Events[0].Kind);
        Equal("out_of_bounds", oobResult.Events[0].Reason);

        // Turn 1 (Seat 1 at (0,0)): move South -> (0,1) is wall
        var wallResult = game.Step(1, GameAction.Move(Direction.South));
        Equal(ActionStatus.Blocked, wallResult.Outcome.Status);
        Equal("wall", wallResult.Outcome.Reason);
        Equal(new Position(0, 0), game.Position(1));
        Equal(2L, game.Tick);
        Equal(0, game.OwnerSeat);
        Equal(EventKind.MoveBlocked, wallResult.Events[0].Kind);
        Equal("wall", wallResult.Events[0].Reason);

        // Turn 2 (Seat 0 at (2,0)): move East -> (3,0) is closed door
        var doorResult = game.Step(0, GameAction.Move(Direction.East));
        Equal(ActionStatus.Blocked, doorResult.Outcome.Status);
        Equal("closed_door", doorResult.Outcome.Reason);
        Equal(new Position(2, 0), game.Position(0));
        Equal(3L, game.Tick);
        Equal(1, game.OwnerSeat);
        Equal(EventKind.MoveBlocked, doorResult.Events[0].Kind);
        Equal("closed_door", doorResult.Events[0].Reason);

        // Turn 3 (Seat 1 at (0,0)): move West -> (-1,0) is out of bounds
        var oobWest = game.Step(1, GameAction.Move(Direction.West));
        Equal(ActionStatus.Blocked, oobWest.Outcome.Status);
        Equal("out_of_bounds", oobWest.Outcome.Reason);
        Equal(new Position(0, 0), game.Position(1));
        Equal(4L, game.Tick);
        Equal(0, game.OwnerSeat);

        return Task.CompletedTask;
    }

    private static Task SharedKeyPathAsync()
    {
        string[] rows = ["......."];
        Position exit = new(0, 0);
        Position key = new(2, 0);
        Position door = new(4, 0);
        Position core = new(6, 0);
        // Seat 0 at (1, 0), Seat 1 at (3, 0)
        var scene = new MultiScenario(rows, [new(1, 0), new(3, 0)], exit, key, door, core);
        var game = MultiGame.Create(scene);

        // Seat 0 moves onto the key at (2, 0)
        game.Step(0, GameAction.Move(Direction.East));
        Equal(new Position(2, 0), game.Position(0));

        // Seat 1 waits at (3, 0) - has never touched the key tile (2, 0)
        game.Step(1, GameAction.Wait());
        Equal(new Position(3, 0), game.Position(1));

        // Seat 0 picks up key
        var pickup = game.Step(0, GameAction.Pickup());
        Equal(ActionStatus.Applied, pickup.Outcome.Status);
        Check(game.Capture().HasKey, "Team key missing after pickup.");
        Check(game.Observe(0).HasKey, "Seat 0 observation missing key.");
        Check(game.Observe(1).HasKey, "Seat 1 observation missing team-shared key.");

        // Seat 1 (never having touched key tile) opens the door at (4, 0)
        var openDoor = game.Step(1, GameAction.Interact(Direction.East));
        Equal(ActionStatus.Applied, openDoor.Outcome.Status);
        Check(game.Capture().DoorOpen, "Door not opened by teammate.");
        Check(game.Capture().HasKey, "Key was consumed when door opened.");

        // Seat 0 waits
        game.Step(0, GameAction.Wait());

        // A third interact on the already opened door reports door_already_open and keeps key
        var thirdInteract = game.Step(1, GameAction.Interact(Direction.East));
        Equal(ActionStatus.NoEffect, thirdInteract.Outcome.Status);
        Equal("door_already_open", thirdInteract.Outcome.Reason);
        Check(game.Capture().DoorOpen, "Door closed after redundant interact.");
        Check(game.Capture().HasKey, "Key consumed after redundant interact.");

        return Task.CompletedTask;
    }

    private static Task CarrierDisciplineAsync()
    {
        string[] rows = [".....", "....."];
        Position exit = new(0, 0);
        Position key = new(1, 0);
        Position door = new(2, 0);
        Position core = new(3, 0);
        var scene = new MultiScenario(rows, [new(3, 1), new(3, 1)], exit, key, door, core);
        var game = MultiGame.Create(scene);

        // Standing at (3, 1), not on core (3, 0) -> pickup gets nothing_to_pick_up
        var failPickup = game.Step(0, GameAction.Pickup());
        Equal(ActionStatus.NoEffect, failPickup.Outcome.Status);
        Equal("nothing_to_pick_up", failPickup.Outcome.Reason);
        Check(game.Capture().CoreHolder is null, "Core picked up while not standing on it.");

        game.Step(1, GameAction.Wait());

        // Seat 0 moves onto core at (3, 0)
        game.Step(0, GameAction.Move(Direction.North));
        Equal(new Position(3, 0), game.Position(0));

        // Seat 1 also moves onto core tile (3, 0)
        game.Step(1, GameAction.Move(Direction.North));
        Equal(new Position(3, 0), game.Position(1));

        // Seat 0 picks up core -> becomes carrier
        var pickCore = game.Step(0, GameAction.Pickup());
        Equal(ActionStatus.Applied, pickCore.Outcome.Status);
        Equal(0, game.Capture().CoreHolder);

        // Seat 1 is standing on the same tile (3, 0), but core is already carried -> nothing_to_pick_up
        var secondPickup = game.Step(1, GameAction.Pickup());
        Equal(ActionStatus.NoEffect, secondPickup.Outcome.Status);
        Equal("nothing_to_pick_up", secondPickup.Outcome.Reason);
        Equal(0, game.Capture().CoreHolder); // Carrier cannot be changed by another seat's pickup

        // Route the non-carrier (seat 1) to the exit along the open row 1 while the carrier
        // (seat 0) deliberately stays away from it. Row 1 is clear floor, so the closed door at
        // (2,0) is irrelevant and no key is needed for this part of the check - an earlier
        // revision tried to walk west through the closed door and failed with "Missing key".
        game.Step(0, GameAction.Move(Direction.South));     // 0 carrier -> (3,1)
        game.Step(1, GameAction.Move(Direction.South));     // 1 -> (3,1)
        game.Step(0, GameAction.Move(Direction.West));      // 0 -> (2,1)
        game.Step(1, GameAction.Move(Direction.West));      // 1 -> (2,1)
        game.Step(0, GameAction.Move(Direction.West));      // 0 -> (1,1)
        game.Step(1, GameAction.Move(Direction.West));      // 1 -> (1,1)
        game.Step(0, GameAction.Move(Direction.West));      // 0 -> (0,1)
        game.Step(1, GameAction.Move(Direction.West));      // 1 -> (0,1)
        game.Step(0, GameAction.Wait());                    // carrier stays at (0,1)
        Equal(0, game.Capture().CoreHolder);
        Check(game.Position(0) != exit, "Carrier is standing on the exit too early.");
        Check(game.Episode is null, "Success occurred while the carrier is away from the exit.");
        Equal(MissionPhase.ReturnToExit, game.Mission);

        // A teammate standing on the exit must not complete the episode.
        game.Step(1, GameAction.Move(Direction.North));     // 1 -> (0,0) exit, not the carrier
        Equal(exit, game.Position(1));
        Check(game.Episode is null, "A non-carrier standing on the exit ended the episode.");

        // Only the carrier reaching the exit wins, and it wins for the whole team.
        var win = game.Step(0, GameAction.Move(Direction.North)); // 0 carrier -> (0,0) exit
        Equal(exit, game.Position(0));
        EpisodeOutcome finished = game.Episode ?? throw new Exception("Carrier on the exit did not end the episode.");
        Equal(EpisodeEndKind.Success, finished.Kind);
        Check(finished.Terminated && !finished.Truncated, "Wrong terminal flags on cooperative success.");
        Equal(MissionPhase.Succeeded, game.Mission);
        Check(win.Events.Any(e => e.Kind == EventKind.Succeeded), "Missing Succeeded domain event.");

        // Truncation without a carrier reports turn_limit
        var truncScene = new MultiScenario(rows, [new(3, 1), new(3, 1)], exit, key, door, core, maxTicks: 2);
        var truncGame = MultiGame.Create(truncScene);
        truncGame.Step(0, GameAction.Wait());
        var lastStep = truncGame.Step(1, GameAction.Wait());
        Equal(2L, truncGame.Tick);
        Check(truncGame.Capture().CoreHolder is null, "Core held unexpectedly.");
        Equal(EpisodeEndKind.TurnLimit, truncGame.Episode!.Kind);
        Check(!truncGame.Episode.Terminated && truncGame.Episode.Truncated, "Turn limit flags mismatch.");
        Equal(EpisodeEndKind.TurnLimit, lastStep.Episode!.Kind);

        return Task.CompletedTask;
    }

    private static Task CooperativeWinAsync()
    {
        string[] rows = [".....", "....."];
        Position exit = new(0, 0);
        Position key = new(1, 0);
        Position door = new(2, 0);
        Position core = new(3, 0);
        // Seat 0 starts at (3, 1), Seat 1 starts on exit (0, 0)
        var scene = new MultiScenario(rows, [new(3, 1), new(0, 0)], exit, key, door, core);
        var game = MultiGame.Create(scene);

        // Turn 0: Seat 0 moves onto Core (3, 0)
        game.Step(0, GameAction.Move(Direction.North));
        // Turn 1: Seat 1 waits on Exit (0, 0)
        game.Step(1, GameAction.Wait());
        // Turn 2: Seat 0 picks up Core -> carrier
        game.Step(0, GameAction.Pickup());
        Equal(0, game.Capture().CoreHolder);

        // Walk carrier to (0, 1) - exactly one step south of exit (0, 0)
        // Seat 1 stays on the exit (0, 0)
        game.Step(1, GameAction.Wait()); // Tick 3
        game.Step(0, GameAction.Move(Direction.South)); // 0 -> (3, 1), Tick 4
        game.Step(1, GameAction.Wait()); // Tick 5
        game.Step(0, GameAction.Move(Direction.West));  // 0 -> (2, 1), Tick 6
        game.Step(1, GameAction.Wait()); // Tick 7
        game.Step(0, GameAction.Move(Direction.West));  // 0 -> (1, 1), Tick 8
        game.Step(1, GameAction.Wait()); // Tick 9
        game.Step(0, GameAction.Move(Direction.West));  // 0 -> (0, 1), Tick 10

        // Carrier is at (0, 1) (one step away from exit), non-carrier is at (0, 0) (already on exit)
        Equal(new Position(0, 1), game.Position(0));
        Equal(new Position(0, 0), game.Position(1));
        Equal(0, game.Capture().CoreHolder);
        Check(game.Episode is null, "Episode ended before carrier reached exit.");
        Equal(MissionPhase.ReturnToExit, game.Mission);

        // Seat 1 (non-carrier) waits on the exit
        game.Step(1, GameAction.Wait()); // Tick 11
        Check(game.Episode is null, "Non-carrier waiting on exit caused early success.");
        Equal(MissionPhase.ReturnToExit, game.Mission);

        // Seat 0 (carrier) moves North onto the exit (0, 0)
        var winStep = game.Step(0, GameAction.Move(Direction.North)); // Tick 12
        Equal(new Position(0, 0), game.Position(0));
        Equal(new Position(0, 0), game.Position(1));
        Check(game.Episode is not null, "Episode did not end when carrier reached exit.");
        // Check() is a plain helper, so the compiler needs the null-forgiving local here.
        EpisodeOutcome finished = game.Episode!;
        Equal(EpisodeEndKind.Success, finished.Kind);
        Check(finished.Terminated, "Expected Episode.Terminated to be true.");
        Check(!finished.Truncated, "Expected Episode.Truncated to be false.");
        Equal(MissionPhase.Succeeded, game.Mission);
        Equal(EpisodeEndKind.Success, winStep.Episode!.Kind);
        Check(winStep.Events.Any(e => e.Kind == EventKind.Succeeded), "Missing Succeeded domain event.");

        return Task.CompletedTask;
    }

    private static Task PerSeatObservationsAsync()
    {
        string[] rows = ["#########", "#.......#", "#########"];
        Position exit = new(1, 1);
        Position key = new(2, 1);
        Position door = new(4, 1);
        Position core = new(6, 1);
        // Radius 2: Seat 0 at (1, 1) sees X in [0, 3], Seat 1 at (7, 1) sees X in [5, 8]
        var scene = new MultiScenario(rows, [new(1, 1), new(7, 1)], exit, key, door, core, visibilityRadius: 2);
        var game = MultiGame.Create(scene);

        var obs0 = game.Observe(0);
        var obs1 = game.Observe(1);

        Equal(0, obs0.Seat);
        Equal(1, obs1.Seat);
        Equal(new Position(1, 1), obs0.Position);
        Equal(new Position(7, 1), obs1.Position);

        // Different tile sets:
        var tiles0 = obs0.Tiles.Select(t => t.Position).ToHashSet();
        var tiles1 = obs1.Tiles.Select(t => t.Position).ToHashSet();
        Check(!tiles0.SetEquals(tiles1), "Observations returned identical tile sets.");

        // Each contains its own position:
        Check(tiles0.Contains(obs0.Position), "obs0 does not contain its own position.");
        Check(tiles1.Contains(obs1.Position), "obs1 does not contain its own position.");

        // Never the other seat's position as its own:
        Check(obs0.Position != game.Position(1), "obs0 took seat 1 position as its own.");
        Check(obs1.Position != game.Position(0), "obs1 took seat 0 position as its own.");
        Check(!tiles0.Contains(obs1.Position), "obs0 leaked seat 1 position.");
        Check(!tiles1.Contains(obs0.Position), "obs1 leaked seat 0 position.");

        // HasCore is true only for the carrier:
        Check(!obs0.HasCore && !obs1.HasCore, "Initial observation has core.");

        // Seat 0 waits
        game.Step(0, GameAction.Wait());
        // Seat 1 moves to core at (6, 1)
        game.Step(1, GameAction.Move(Direction.West));
        // Seat 0 waits
        game.Step(0, GameAction.Wait());
        // Seat 1 picks up the core -> becomes carrier
        game.Step(1, GameAction.Pickup());
        Equal(1, game.Capture().CoreHolder);

        var after0 = game.Observe(0);
        var after1 = game.Observe(1);
        Check(!after0.HasCore, "Non-carrier seat 0 observation reports HasCore.");
        Check(after1.HasCore, "Carrier seat 1 observation does not report HasCore.");

        return Task.CompletedTask;
    }

    private static Task SnapshotImmutabilityAndHashAsync()
    {
        string[] rows = ["#########", "#.......#", "#########"];
        var scene = new MultiScenario(rows, [new(1, 1), new(7, 1)], new(1, 1), new(2, 1), new(4, 1), new(6, 1));
        var game = MultiGame.Create(scene);

        // Capturing two snapshots around a step keeps the earlier one unchanged and its hash stable
        var snapBefore = game.Capture();
        string hashBefore = MultiStateEncoding.Hash(snapBefore);
        Equal(0L, snapBefore.Tick);
        Equal(new Position(1, 1), snapBefore.Positions[0]);
        Equal(new Position(7, 1), snapBefore.Positions[1]);

        game.Step(0, GameAction.Move(Direction.East)); // Seat 0 -> (2, 1)

        var snapAfter = game.Capture();
        string hashAfter = MultiStateEncoding.Hash(snapAfter);

        // Earlier snapshot remains unchanged and its hash remains identical
        Equal(0L, snapBefore.Tick);
        Equal(new Position(1, 1), snapBefore.Positions[0]);
        Equal(new Position(7, 1), snapBefore.Positions[1]);
        Equal(hashBefore, MultiStateEncoding.Hash(snapBefore));

        // Later snapshot reflects new state and differs in hash
        Equal(1L, snapAfter.Tick);
        Equal(new Position(2, 1), snapAfter.Positions[0]);
        Check(hashBefore != hashAfter, "Hash did not change across step.");

        // MultiSnapshot exposes ImmutableArray<Position> and ImmutableArray<MultiActionResult?>.
        // Attempting to mutate through the IList<T> interface throws NotSupportedException,
        // leaving the snapshot and its hash intact.
        Throws<NotSupportedException>(() => ((IList<Position>)snapBefore.Positions)[0] = new Position(9, 9));
        Throws<NotSupportedException>(() => ((IList<MultiActionResult?>)snapBefore.LastResults)[0] = new MultiActionResult(ActionStatus.Applied));
        Throws<NotSupportedException>(() => ((IList<Position>)snapBefore.Positions).Add(new Position(0, 0)));

        Equal(new Position(1, 1), snapBefore.Positions[0]);
        Equal(null, snapBefore.LastResults[0]);
        Equal(hashBefore, MultiStateEncoding.Hash(snapBefore));

        return Task.CompletedTask;
    }

    private static Task SpawnValidationRejectionsAsync()
    {
        string[] rows = ["#########", "#.......#", "#########"];
        Position exit = new(1, 1);
        Position key = new(2, 1);
        Position door = new(4, 1);
        Position core = new(6, 1);

        // 1. Spawns out of bounds
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(-1, 1), new(3, 1)], exit, key, door, core));
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(1, -1), new(3, 1)], exit, key, door, core));
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(1, 1), new(99, 1)], exit, key, door, core));
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(1, 1), new(3, 99)], exit, key, door, core));

        // 2. Spawns on a wall
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(0, 0), new(3, 1)], exit, key, door, core));
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(1, 1), new(0, 1)], exit, key, door, core));

        // 3. Spawns overlapping the key
        Throws<ArgumentException>(() => new MultiScenario(rows, [key, new(3, 1)], exit, key, door, core));
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(1, 1), key], exit, key, door, core));

        // 4. Spawns overlapping the door
        Throws<ArgumentException>(() => new MultiScenario(rows, [door, new(3, 1)], exit, key, door, core));
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(1, 1), door], exit, key, door, core));

        // 5. Spawns overlapping the core
        Throws<ArgumentException>(() => new MultiScenario(rows, [core, new(3, 1)], exit, key, door, core));
        Throws<ArgumentException>(() => new MultiScenario(rows, [new(1, 1), core], exit, key, door, core));

        // 6. Empty spawn list
        Throws<ArgumentException>(() => new MultiScenario(rows, [], exit, key, door, core));

        // 7. More than 4 spawns (5 spawns)
        Throws<ArgumentException>(() => new MultiScenario(rows,
            [new(1, 1), new(3, 1), new(5, 1), new(7, 1), new(1, 1)], exit, key, door, core));

        // 8. Valid 2-seat scenario constructs successfully
        var valid = new MultiScenario(rows, [new(1, 1), new(7, 1)], exit, key, door, core);
        Equal(2, valid.SeatCount);
        Equal(new Position(1, 1), valid.Spawns[0]);
        Equal(new Position(7, 1), valid.Spawns[1]);
        Check(valid.Layout is not null, "Layout is null on valid scenario.");

        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Throws<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
