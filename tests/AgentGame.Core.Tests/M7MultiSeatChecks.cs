using System.Collections.Immutable;
using System.Text.Json;
using AgentGame.Core;
using AgentGame.Core.Multiplayer;

namespace AgentGame.Core.Tests;

/// <summary>
/// M7 multi-seat Core contract checks for facility-zero/2 + core-state/2. Console style, zero
/// external dependencies, matching the rest of this project. Every check throws on failure and is
/// run by RunAsync, which prints PASS/FAIL lines and returns the failure count.
/// </summary>
public static class M7MultiSeatChecks
{
    // Frozen values produced independently by tests/Fixtures/Core/generate_vectors_v2.py.
    private const string FrozenInitialHash = "d9e985318403061163239a6e14178acb4a2d3bbec084e994f83c66842ceb6ac5";
    private const string FrozenCarrierHash = "348965c6bce99d8eb3887288421ce7ac086c50e149976743c20f7952533dd631";
    private const string FrozenSuccessHash = "aa64b569d1d718a5ee227386a93a280829f766160102a2e1f0589ff194231bf8";

    // Fixed 2-seat fixture shared with generate_vectors_v2.py.
    private static readonly string[] Rows = ["#########", "#.......#", "#########"];
    private static MultiScenario GoldenScenario() =>
        new(Rows, [new(1, 1), new(7, 1)], new(1, 1), new(2, 1), new(4, 1), new(6, 1));

    public static async Task<int> RunAsync(string root)
    {
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Rotation is a pure tick-to-seat map and out-of-turn steps throw without mutation", RotationAsync),
            ("Same scenario and action sequence reproduce identical per-step hashes and events", DeterminismAsync),
            ("Hash covers every seat: spawn and current position both change it", HashCoversSeatsAsync),
            ("Cooperative task succeeds only when the carrier returns to the exit", CooperativeTaskAsync),
            ("A teammate standing on the exit does not end the episode", NonCarrierExitNoEndAsync),
            ("The core has exactly one carrier", SingleCarrierAsync),
            ("The encoding distinguishes nobody / seat 0 / seat 1 carriers", CarrierEncodingDistinctionAsync),
            ("Success on the final permitted seat-turn wins over the shared budget; excess truncates", FinalTurnPriorityAsync),
            ("Each seat's observation never leaks tiles or state only the other seat can see", InformationIsolationAsync),
            ("Captured snapshots stay immutable while the owning game keeps stepping", ImmutabilityAsync),
            ("After success or truncation every seat rejects further steps unchanged", TerminatedRejectionAsync),
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M7 Multi-seat: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static Task RotationAsync()
    {
        var game = MultiGame.Create(GoldenScenario());
        Equal(0, game.OwnerSeat);
        string initialHash = MultiStateEncoding.Hash(game.Capture());
        // Acting out of turn must throw and must not change tick, positions or hash.
        Throws<InvalidOperationException>(() => game.Step(1, GameAction.Wait()));
        Equal(0L, game.Tick); Equal(new Position(7, 1), game.Position(1)); Equal(initialHash, MultiStateEncoding.Hash(game.Capture()));

        var first = game.Step(0, GameAction.Wait());
        Equal(0, first.Seat); Equal(1L, first.Tick); Equal(1, game.OwnerSeat);
        string afterFirst = MultiStateEncoding.Hash(game.Capture());
        Throws<InvalidOperationException>(() => game.Step(0, GameAction.Wait()));
        Equal(1L, game.Tick); Equal(afterFirst, MultiStateEncoding.Hash(game.Capture()));
        var second = game.Step(1, GameAction.Wait());
        Equal(1, second.Seat); Equal(2L, second.Tick); Equal(0, game.OwnerSeat);

        // 3-seat variant: the seat map cycles 0,1,2,0,...
        var three = MultiGame.Create(new MultiScenario(Rows, [new(1, 1), new(7, 1), new(1, 1)],
            new(1, 1), new(2, 1), new(4, 1), new(6, 1)));
        for (int tick = 0; tick < 6; tick++)
        {
            int owner = tick % 3;
            Equal(owner, three.OwnerSeat);
            var result = three.Step(owner, GameAction.Wait());
            Equal(owner, result.Seat); Equal(tick + 1L, result.Tick);
        }

        // Seat count bounds: 0 and 5 rejected; spawn on wall / on the shared core rejected.
        Throws<ArgumentException>(() => new MultiScenario(Rows, [], new(1, 1), new(2, 1), new(4, 1), new(6, 1)));
        Throws<ArgumentException>(() => new MultiScenario(Rows,
            [new(1, 1), new(8, 1), new(3, 1), new(5, 1), new(7, 1)], new(1, 1), new(2, 1), new(4, 1), new(6, 1)));
        Throws<ArgumentException>(() => new MultiScenario(Rows, [new(0, 1)], new(1, 1), new(2, 1), new(4, 1), new(6, 1))); // on wall
        Throws<ArgumentException>(() => new MultiScenario(Rows, [new(6, 1)], new(1, 1), new(2, 1), new(4, 1), new(6, 1))); // on core
        return Task.CompletedTask;
    }

    private static Task DeterminismAsync()
    {
        var route = new (int Seat, GameAction Action)[]
        {
            (0, GameAction.Move(Direction.East)),
            (1, GameAction.Wait()),
            (0, GameAction.Pickup()),
            (1, GameAction.Move(Direction.West)),
            (0, GameAction.Move(Direction.East)),
            (1, GameAction.Move(Direction.West)),
        };
        var first = RunSequence(route);
        var second = RunSequence(route);
        Check(first.Hashes.SequenceEqual(second.Hashes), "Per-step hashes diverged across identical replays.");
        Check(first.Events.Count == second.Events.Count &&
              first.Events.Zip(second.Events).All(p => p.First.SequenceEqual(p.Second)),
            "Event sequences diverged across identical replays.");
        return Task.CompletedTask;
    }

    private static (List<string> Hashes, List<ImmutableArray<DomainEvent>> Events) RunSequence((int, GameAction)[] route)
    {
        var game = MultiGame.Create(GoldenScenario());
        var hashes = new List<string>();
        var events = new List<ImmutableArray<DomainEvent>>();
        foreach (var (seat, action) in route)
        {
            var result = game.Step(seat, action);
            hashes.Add(MultiStateEncoding.Hash(game.Capture()));
            events.Add(result.Events);
        }
        return (hashes, events);
    }

    private static Task HashCoversSeatsAsync()
    {
        // (a) Two scenarios differing only in one seat's spawn produce different initial hashes.
        // Seat 1 spawn differs (7,1) vs (3,1).
        var a = MultiGame.Create(GoldenScenario());
        var b = MultiGame.Create(new MultiScenario(Rows, [new(1, 1), new(3, 1)], new(1, 1), new(2, 1), new(4, 1), new(6, 1)));
        Check(MultiStateEncoding.Hash(a.Capture()) != MultiStateEncoding.Hash(b.Capture()),
            "Different seat-1 spawn produced the same initial hash.");

        // (b) A seat moving changes the hash even when the other seat's position is unchanged.
        string initial = MultiStateEncoding.Hash(a.Capture());
        a.Step(0, GameAction.Move(Direction.East)); // seat 0 -> (2,1), seat 1 still (7,1)
        Equal(new Position(2, 1), a.Position(0)); Equal(new Position(7, 1), a.Position(1));
        Check(MultiStateEncoding.Hash(a.Capture()) != initial, "Seat 0 move did not change the hash.");
        return Task.CompletedTask;
    }

    private static Task CooperativeTaskAsync()
    {
        var game = MultiGame.Create(GoldenScenario());
        Equal(FrozenInitialHash, MultiStateEncoding.Hash(game.Capture()));
        // The frozen vectors must also literally match the independent Python file, not just our
        // hardcoded copy, so a stale JSON cannot silently drift from what the checks assert.
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests", "Fixtures", "Core", "core-golden-v2.json")));
        var data = json.RootElement;
        Equal(data.GetProperty("initial_sha256").GetString(), FrozenInitialHash);
        Equal(data.GetProperty("initial_sha256").GetString(), MultiStateEncoding.Hash(game.Capture()));
        Equal(data.GetProperty("initial_hex").GetString(), Convert.ToHexStringLower(MultiStateEncoding.Encode(game.Capture())));

        // Golden "carrier" intermediate: seat1 walks onto the core and picks it up, becoming the
        // sole carrier; seat0 holds the shared key. No seat is on the exit, so no episode end.
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (2,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (6,1)
        game.Step(0, GameAction.Pickup());               // shared key
        game.Step(1, GameAction.Pickup());               // seat1 becomes the carrier
        var snapshot = game.Capture();
        Equal(4L, snapshot.Tick);
        Equal(new Position(2, 1), snapshot.Positions[0]); Equal(new Position(6, 1), snapshot.Positions[1]);
        Check(snapshot.HasKey && !snapshot.DoorOpen, "Unexpected shared inventory at golden point.");
        Equal(1, snapshot.CoreHolder); Check(game.Episode is null, "Episode ended with nobody on the exit.");
        Check(game.Mission == MissionPhase.ReturnToExit, "Carrier present but phase is not ReturnToExit.");
        Equal(FrozenCarrierHash, MultiStateEncoding.Hash(snapshot));
        Equal(data.GetProperty("carrier_sha256").GetString(), MultiStateEncoding.Hash(snapshot));
        Equal(data.GetProperty("carrier_hex").GetString(), Convert.ToHexStringLower(MultiStateEncoding.Encode(snapshot)));

        game.Step(0, GameAction.Move(Direction.East));  // 0 -> (3,1)
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (5,1)
        game.Step(0, GameAction.Interact(Direction.East)); // 0 opens the shared door at (4,1)
        Check(game.Capture().DoorOpen && game.Capture().HasKey && game.Capture().CoreHolder == 1,
            "Door did not open, the key was consumed, or the carrier changed.");
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (4,1)
        game.Step(0, GameAction.Move(Direction.East));  // 0 -> (4,1)
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (3,1)
        game.Step(0, GameAction.Move(Direction.West));  // 0 -> (3,1)
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (2,1)
        game.Step(0, GameAction.Move(Direction.West));  // 0 -> (2,1)
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (1,1) exit: carrier reaches exit
        Equal(EpisodeEndKind.Success, game.Episode!.Kind);
        Equal(14L, game.Tick);

        var success = game.Capture();
        Equal(1, success.CoreHolder); Equal(new Position(1, 1), success.Positions[1]);
        Equal(FrozenSuccessHash, MultiStateEncoding.Hash(success));
        Equal(data.GetProperty("success_sha256").GetString(), MultiStateEncoding.Hash(success));
        Equal(data.GetProperty("success_hex").GetString(), Convert.ToHexStringLower(MultiStateEncoding.Encode(success)));
        return Task.CompletedTask;
    }

    private static Task NonCarrierExitNoEndAsync()
    {
        // Seat 1 (a non-carrier) reaches the exit first; seat 0 then picks up the core. A
        // non-carrier teammate standing on the exit must NOT complete the episode - only the
        // carrier (seat 0) reaching the exit succeeds.
        var game = MultiGame.Create(GoldenScenario());
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (2,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (6,1)
        game.Step(0, GameAction.Pickup());               // key
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (5,1)
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (3,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (4,1) blocked (door still closed)
        game.Step(0, GameAction.Interact(Direction.East)); // 0 opens the door at (4,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (4,1)
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (4,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (3,1)
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (5,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (2,1)
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (6,1) on core
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (1,1) exit (non-carrier)
        Equal(null, game.Capture().CoreHolder);
        Check(game.Episode is null, "Episode ended while nobody carried the core.");
        game.Step(0, GameAction.Pickup());               // seat0 becomes the carrier, seat1 on exit
        Equal(0, game.Capture().CoreHolder);
        Equal(new Position(1, 1), game.Position(1));
        Check(game.Episode is null, "Non-carrier teammate standing on the exit ended the episode.");
        Check(game.Mission == MissionPhase.ReturnToExit, "Carrier exists but phase is wrong.");
        string hash = MultiStateEncoding.Hash(game.Capture());
        long tick = game.Tick;

        // A non-carrier teammate keeps the episode alive: tick/hash keep advancing while the
        // carrier (seat 0 at (6,1)) has not returned to the exit.
        game.Step(1, GameAction.Wait());                 // 1 waits on the exit
        game.Step(0, GameAction.Move(Direction.West));   // 0 -> (5,1)
        Check(game.Episode is null, "Carrier not on exit but episode ended.");
        Check(game.Tick > tick && MultiStateEncoding.Hash(game.Capture()) != hash,
            "Tick/hash did not advance while the carrier was away from the exit.");
        Equal(0, game.Capture().CoreHolder);

        // Now walk the carrier (seat 0) onto the exit to complete the episode.
        game.Step(1, GameAction.Wait());                 // 1 waits on the exit
        game.Step(0, GameAction.Move(Direction.West));   // 0 -> (4,1)
        game.Step(1, GameAction.Wait());
        game.Step(0, GameAction.Move(Direction.West));   // 0 -> (3,1)
        game.Step(1, GameAction.Wait());
        game.Step(0, GameAction.Move(Direction.West));   // 0 -> (2,1)
        game.Step(1, GameAction.Wait());
        game.Step(0, GameAction.Move(Direction.West));   // 0 -> (1,1) exit: carrier arrives
        Equal(EpisodeEndKind.Success, game.Episode!.Kind);
        return Task.CompletedTask;
    }

    private static Task SingleCarrierAsync()
    {
        // A second seat standing on the core tile cannot pick it up again once a seat already
        // holds it, the holder never changes until the episode ends, and a seat's observation
        // reports HasCore only for the carrier.
        var game = MultiGame.Create(GoldenScenario());
        game.Step(0, GameAction.Move(Direction.East));  // 0 -> (2,1)
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (6,1)
        game.Step(0, GameAction.Pickup());              // key
        game.Step(1, GameAction.Pickup());              // seat1 becomes the carrier
        Equal(1, game.Capture().CoreHolder);

        // seat0 crosses the (now open) door to reach the core tile while seat1 (the carrier)
        // shuttles on the right side and never touches the exit, so the episode stays alive.
        game.Step(0, GameAction.Move(Direction.East));  // 0 -> (3,1)
        game.Step(1, GameAction.Move(Direction.East));  // 1 -> (7,1)
        game.Step(0, GameAction.Interact(Direction.East)); // 0 opens the door at (4,1)
        Equal(1, game.Capture().CoreHolder); Check(game.Episode is null, "Carrier escaped the corridor.");
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (6,1)
        game.Step(0, GameAction.Move(Direction.East));  // 0 -> (4,1)
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (5,1)
        game.Step(0, GameAction.Move(Direction.East));  // 0 -> (5,1)
        game.Step(1, GameAction.Move(Direction.West));  // 1 -> (4,1)
        game.Step(0, GameAction.Move(Direction.East));  // 0 -> (6,1) on core, already carried
        Equal(1, game.Capture().CoreHolder); Equal(new Position(6, 1), game.Position(0));
        game.Step(1, GameAction.Wait());                // 1 waits (nowhere near the exit)

        // A second seat standing on the core tile cannot pick it up again.
        var refuse = game.Step(0, GameAction.Pickup());
        Equal(ActionStatus.NoEffect, refuse.Outcome.Status); Equal("nothing_to_pick_up", refuse.Outcome.Reason);
        Equal(1, game.Capture().CoreHolder);
        Check(game.Episode is null, "Episode ended while the carrier sat off the exit.");
        Check(!game.Observe(0).HasCore || game.Capture().CoreHolder == 0, "Non-carrier reports HasCore.");
        Check(game.Observe(1).HasCore, "Carrier seat 1 does not report HasCore.");
        Check(game.Observe(0).HasKey && game.Observe(1).HasKey, "Key is not team-shared.");
        return Task.CompletedTask;
    }

    private static Task CarrierEncodingDistinctionAsync()
    {
        // Nobody / seat 0 / seat 1 carriers must produce three different hashes even when every
        // position is identical. We fix both seats at the same tile and vary only the holder.
        var scene = new MultiScenario(Rows, [new(1, 1), new(7, 1)],
            new(1, 1), new(2, 1), new(4, 1), new(6, 1));
        var positions = ImmutableArray.Create(new Position(3, 1), new Position(3, 1));
        var none = new MultiSnapshot(scene, 0, positions, HasKey: false, DoorOpen: false,
            CoreHolder: null, ImmutableArray<MultiActionResult?>.Empty, Episode: null);
        var holder0 = none with { CoreHolder = 0 };
        var holder1 = none with { CoreHolder = 1 };
        string hNone = MultiStateEncoding.Hash(none);
        string h0 = MultiStateEncoding.Hash(holder0);
        string h1 = MultiStateEncoding.Hash(holder1);
        Check(hNone != h0 && hNone != h1 && h0 != h1,
            "Holder index did not distinguish the state encoding (nobody/0/1 collided).");
        return Task.CompletedTask;
    }

    private static Task FinalTurnPriorityAsync()
    {
        // Single-row 5-wide map: exit(0,0), key(1,0), door(2,0), core(3,0); seats at 0 and 4.
        string[] rows = ["....."];
        var route = new (int Seat, GameAction Action)[]
        {
            (0, GameAction.Move(Direction.East)),   // 0 -> (1,0) on key
            (1, GameAction.Move(Direction.West)),   // 1 -> (3,0) on core
            (0, GameAction.Pickup()),               // key
            (1, GameAction.Pickup()),               // core
            (0, GameAction.Interact(Direction.East)), // open door at (2,0)
            (1, GameAction.Move(Direction.West)),   // 1 -> (2,0)
            (0, GameAction.Move(Direction.East)),   // 0 -> (2,0)
            (1, GameAction.Move(Direction.West)),   // 1 -> (1,0)
            (0, GameAction.Move(Direction.East)),   // 0 -> (3,0)
            (1, GameAction.Move(Direction.West)),   // 1 -> (0,0) exit holding core
        };
        // Success on the final permitted seat-turn must win over the shared budget (tick 10 == max).
        var won = MultiGame.Create(new MultiScenario(rows, [new(0, 0), new(4, 0)],
            new(0, 0), new(1, 0), new(2, 0), new(3, 0), 10));
        foreach (var (seat, action) in route) won.Step(seat, action);
        Equal(EpisodeEndKind.Success, won.Episode!.Kind); Equal(10L, won.Tick);

        // One tick too few truncates the same route to turn_limit on the shared budget rule.
        var truncated = MultiGame.Create(new MultiScenario(rows, [new(0, 0), new(4, 0)],
            new(0, 0), new(1, 0), new(2, 0), new(3, 0), 9));
        foreach (var (seat, action) in route.Take(9)) truncated.Step(seat, action);
        Equal(9L, truncated.Tick);
        Equal(EpisodeEndKind.TurnLimit, truncated.Episode!.Kind);
        Check(!truncated.Episode.Terminated && truncated.Episode.Truncated, "Turn-limit flags wrong.");
        return Task.CompletedTask;
    }

    private static Task InformationIsolationAsync()
    {
        // seat0 at (1,1), seat1 at (7,1): radius 3. seat0 sees x<=4; seat1 sees x>=4.
        var game = MultiGame.Create(GoldenScenario());
        game.Step(0, GameAction.Wait()); // seat0 owns a feedback entry; seat1 has none yet.
        var seat0 = game.Observe(0);
        var seat1 = game.Observe(1);

        Equal(0, seat0.Seat); Equal(new Position(1, 1), seat0.Position);
        Equal(1, seat1.Seat); Equal(new Position(7, 1), seat1.Position);
        Equal(ActionStatus.Applied, seat0.LastResult!.Status);  // seat0 just waited
        Equal(null, seat1.LastResult);                          // seat1 has not acted

        Check(!seat0.Tiles.Any(t => t.Position.X >= 5), "seat0 sees a tile the other seat's side can reveal.");
        Check(!seat0.Tiles.Any(t => t.Position == new Position(7, 1)), "seat0 sees seat1's own position.");
        Check(seat1.Tiles.Any(t => t.Position == new Position(6, 1) && t.Item == ItemKind.Core),
            "seat1 cannot see the core, breaking the isolation probe setup.");
        // A tile visible only to seat1 (the core, x=6) must never leak into seat0's observation.
        Check(!seat0.Tiles.Any(t => t.Position == new Position(6, 1)), "seat0 leaks the far core tile.");
        return Task.CompletedTask;
    }

    private static Task ImmutabilityAsync()
    {
        var game = MultiGame.Create(GoldenScenario());
        var snapshot = game.Capture();
        string hash = MultiStateEncoding.Hash(snapshot);
        Equal(0L, snapshot.Tick); Equal(new Position(1, 1), snapshot.Positions[0]); Equal(new Position(7, 1), snapshot.Positions[1]);

        // The captured arrays must not alias the game's mutable internals: mutating through a
        // writable interface must be rejected, and later steps must not change the snapshot values.
        Throws<NotSupportedException>(() => ((IList<Position>)snapshot.Positions).Add(new(0, 0)));
        game.Step(0, GameAction.Move(Direction.East));
        game.Step(1, GameAction.Move(Direction.West));
        game.Step(0, GameAction.Pickup());

        Equal(0L, snapshot.Tick);
        Equal(new Position(1, 1), snapshot.Positions[0]); Equal(new Position(7, 1), snapshot.Positions[1]);
        Check(!snapshot.HasKey && !snapshot.DoorOpen && snapshot.CoreHolder is null, "Old snapshot mutated by later steps.");
        Equal(hash, MultiStateEncoding.Hash(snapshot));
        Check(snapshot.LastResults.All(r => r is null), "Old per-seat feedback mutated.");
        return Task.CompletedTask;
    }

    private static Task TerminatedRejectionAsync()
    {
        // Truncation: max_ticks=1 ends after seat0's single turn.
        var truncated = MultiGame.Create(new MultiScenario(Rows, [new(1, 1), new(7, 1)],
            new(1, 1), new(2, 1), new(4, 1), new(6, 1), 1));
        truncated.Step(0, GameAction.Wait());
        Equal(EpisodeEndKind.TurnLimit, truncated.Episode!.Kind);
        string hash = MultiStateEncoding.Hash(truncated.Capture());
        Throws<InvalidOperationException>(() => truncated.Step(0, GameAction.Wait()));
        Throws<InvalidOperationException>(() => truncated.Step(1, GameAction.Wait()));
        Equal(hash, MultiStateEncoding.Hash(truncated.Capture()));

        // Success: finish the cooperative task then every seat rejects further steps unchanged.
        var won = MultiGame.Create(GoldenScenario());
        CooperativeActions(won);
        Equal(EpisodeEndKind.Success, won.Episode!.Kind);
        hash = MultiStateEncoding.Hash(won.Capture());
        Throws<InvalidOperationException>(() => won.Step(0, GameAction.Wait()));
        Throws<InvalidOperationException>(() => won.Step(1, GameAction.Wait()));
        Equal(hash, MultiStateEncoding.Hash(won.Capture()));
        return Task.CompletedTask;
    }

    private static void CooperativeActions(MultiGame game)
    {
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (2,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (6,1)
        game.Step(0, GameAction.Pickup());               // key
        game.Step(1, GameAction.Pickup());               // core
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (3,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (5,1)
        game.Step(0, GameAction.Interact(Direction.East)); // open door
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (4,1)
        game.Step(0, GameAction.Move(Direction.East));   // 0 -> (4,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (3,1)
        game.Step(0, GameAction.Move(Direction.West));   // 0 -> (3,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (2,1)
        game.Step(0, GameAction.Move(Direction.West));   // 0 -> (2,1)
        game.Step(1, GameAction.Move(Direction.West));   // 1 -> (1,1) exit -> success
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action operation) where T : Exception
    { try { operation(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "parallel-work.md"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new Exception("Cannot locate fixture root.");
    }
}