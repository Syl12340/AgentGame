using System.Collections.Immutable;
using System.Text.Json;
using AgentGame.Core;
using AgentGame.Core.Tests;

var tests = new (string Name, Action Run)[]
{
    ("Reference route succeeds only after returning", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        var route = Route();
        for (int i = 0; i < route.Length; i++)
        {
            StepResult result = game.Step(route[i]);
            Equal(ActionStatus.Applied, result.Outcome.Status);
            Equal((long)i + 1, result.Tick);
            if (i < route.Length - 1) Check(result.Episode is null, "Early success.");
            if (i == 7) Equal(MissionPhase.ReturnToExit, game.Mission);
        }
        Equal(EpisodeEndKind.Success, game.Episode!.Kind);
        Check(game.Episode.Terminated && !game.Episode.Truncated, "Success flags.");
        Equal(MissionPhase.Succeeded, game.Mission);
        Equal(new Position(1, 1), game.Position);
        Check(game.Capture().HasKey && game.Capture().HasCore && game.Capture().DoorOpen, "Inventory retained.");
    }),
    ("Final permitted turn succeeds before truncation", () =>
    {
        var game = Game.Create(Load("facility-small.json", 13));
        foreach (var action in Route()) game.Step(action);
        Equal(13L, game.Tick);
        Equal(EpisodeEndKind.Success, game.Episode!.Kind);
    }),
    ("Turn limit is truncation and not game success", () =>
    {
        var game = Game.Create(Load("facility-small.json", 12));
        foreach (var action in Route().Take(12)) game.Step(action);
        Equal(EpisodeEndKind.TurnLimit, game.Episode!.Kind);
        Check(!game.Episode.Terminated && game.Episode.Truncated, "Turn-limit flags.");
        Throws<InvalidOperationException>(() => game.Step(GameAction.Wait()));
    }),
    ("Wall collision consumes a turn", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        var result = game.Step(GameAction.Move(Direction.North));
        Equal(new Position(1, 1), game.Position); Equal(1L, game.Tick);
        Equal(ActionStatus.Blocked, result.Outcome.Status); Equal("wall", result.Outcome.Reason);
        Equal(EventKind.MoveBlocked, result.Events[0].Kind);
    }),
    ("Empty pickup consumes a turn", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        var result = game.Step(GameAction.Pickup());
        Equal(ActionStatus.NoEffect, result.Outcome.Status); Equal(1L, game.Tick);
        Equal("nothing_to_pick_up", result.Outcome.Reason);
    }),
    ("Closed door blocks movement and missing key blocks interaction", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        game.Step(GameAction.Move(Direction.East)); game.Step(GameAction.Move(Direction.East));
        Equal("missing_key", game.Step(GameAction.Interact(Direction.East)).Outcome.Reason);
        Equal("closed_door", game.Step(GameAction.Move(Direction.East)).Outcome.Reason);
        Equal(new Position(3, 1), game.Position); Equal(4L, game.Tick);
        Check(!game.Capture().DoorOpen, "Closed door changed.");
    }),
    ("No-door interaction consumes a turn", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        Equal("no_door", game.Step(GameAction.Interact(Direction.North)).Outcome.Reason);
        Equal(1L, game.Tick);
    }),
    ("Opening an open door has no effect; key is retained", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        foreach (var action in Route().Take(4)) game.Step(action);
        Equal("door_already_open", game.Step(GameAction.Interact(Direction.East)).Outcome.Reason);
        Check(game.Capture().HasKey && game.Capture().DoorOpen, "Key consumed or door closed.");
        Equal(5L, game.Tick);
    }),
    ("Wait is an applied turn", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        var result = game.Step(GameAction.Wait());
        Equal(ActionStatus.Applied, result.Outcome.Status); Equal(1L, result.Tick);
        Equal(EventKind.Waited, result.Events[0].Kind);
    }),
    ("Invalid domain actions leave state unchanged", () =>
    {
        var game = Game.Create(Load("facility-small.json")); string hash = StateEncoding.Hash(game.Capture());
        foreach (var action in new[] { new GameAction((ActionKind)99), new GameAction(ActionKind.Move),
            GameAction.Move((Direction)99), new GameAction(ActionKind.Wait, Direction.East) })
            Throws<ArgumentException>(() => game.Step(action));
        Equal(0L, game.Tick); Equal(hash, StateEncoding.Hash(game.Capture()));
    }),
    ("Terminal Step rejects without mutation", () =>
    {
        var game = Game.Create(Load("facility-small.json", 1)); game.Step(GameAction.Wait());
        string hash = StateEncoding.Hash(game.Capture());
        Throws<InvalidOperationException>(() => game.Step(GameAction.Move(Direction.East)));
        Equal(hash, StateEncoding.Hash(game.Capture()));
    }),
    ("Boundary is blocked with a valid domain action", () =>
    {
        var scene = new Scenario(["...", "..."], new(0, 0), new(0, 0), new(1, 0), new(2, 0), new(0, 1));
        var game = Game.Create(scene);
        Equal("out_of_bounds", game.Step(GameAction.Move(Direction.North)).Outcome.Reason);
        Equal(1L, game.Tick);
    }),
    ("Pickup changes phase and removes only the visible item", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        Check(game.Observe().Tiles.Any(t => t.Item == ItemKind.Key), "Key not visible initially.");
        game.Step(GameAction.Move(Direction.East)); var result = game.Step(GameAction.Pickup());
        Equal(MissionPhase.OpenDoor, game.Mission);
        Check(result.Events.Select(e => e.Kind).SequenceEqual([EventKind.PickedUp, EventKind.MissionPhaseChanged]), "Event order.");
        Check(game.Observe().Tiles.Single(t => t.Position == game.Position).Item is null, "Picked item still visible.");
    }),
    ("Closed door visible but tiles behind it hidden", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        foreach (var action in Route().Take(3)) game.Step(action);
        var tiles = game.Observe().Tiles;
        Equal(false, tiles.Single(t => t.Position == new Position(4, 1)).DoorOpen!.Value);
        Check(!tiles.Any(t => t.Position == new Position(5, 1) || t.Item == ItemKind.Core), "Door leaks hidden tiles.");
    }),
    ("Opening door immediately changes visibility", () =>
    {
        var game = Game.Create(Load("facility-small.json"));
        foreach (var action in Route().Take(4)) game.Step(action);
        var tiles = game.Observe().Tiles;
        Check(tiles.Any(t => t.Position == new Position(6, 1) && t.Item == ItemKind.Core), "Open door still blocks sight.");
        Equal(true, tiles.Single(t => t.Position == new Position(4, 1)).DoorOpen!.Value);
    }),
    ("Diagonal wall corner cannot leak sight", () =>
    {
        var game = Game.Create(Load("visibility-corners.json")); var tiles = game.Observe().Tiles;
        Check(tiles.Any(t => t.Position == new Position(2, 1) && t.Terrain == Terrain.Wall), "Adjacent blocker hidden.");
        Check(tiles.Any(t => t.Position == new Position(1, 2) && t.Terrain == Terrain.Wall), "Adjacent blocker hidden.");
        Check(!tiles.Any(t => t.Position == new Position(2, 2)), "Corner gap leaks sight.");
        Check(!tiles.Any(t => t.Item == ItemKind.Core), "Hidden core leaked.");
    }),
    ("Single wall also blocks a tied diagonal crossing", () =>
    {
        var scene = new Scenario(["#######", "#.#...#", "#.....#", "#.....#", "#.....#", "#.....#", "#######"],
            new(1, 1), new(1, 1), new(1, 5), new(5, 1), new(5, 5));
        Check(!Game.Create(scene).Observe().Tiles.Any(t => t.Position == new Position(2, 2)), "Conservative tie violated.");
    }),
    ("Chebyshev radius is inclusive, corner rays work on clear floor", () =>
    {
        var scene = new Scenario([".......", ".......", ".......", ".......", ".......", ".......", "......."],
            new(1, 1), new(1, 1), new(0, 6), new(6, 0), new(6, 6));
        var tiles = Game.Create(scene).Observe().Tiles;
        Check(tiles.Any(t => t.Position == new Position(4, 4)), "Radius boundary excluded.");
        Check(!tiles.Any(t => t.Position == new Position(5, 1)), "Outside radius included.");
        Check(tiles.Select(t => t.Position.Y * scene.Width + t.Position.X).SequenceEqual(
            tiles.Select(t => t.Position.Y * scene.Width + t.Position.X).Order()), "Visible tile order unstable.");
    }),
    ("Radius zero reveals only current cell", () =>
    {
        var scene = new Scenario(["....."], new(0, 0), new(0, 0), new(1, 0), new(2, 0), new(3, 0), visibilityRadius: 0);
        Equal(1, Game.Create(scene).Observe().Tiles.Length);
    }),
    ("Old snapshots and observations remain independent", () =>
    {
        string[] rows = ["#########", "#.......#", "#########"];
        var scene = new Scenario(rows, new(1, 1), new(1, 1), new(2, 1), new(4, 1), new(6, 1));
        var game = Game.Create(scene); var snapshot = game.Capture(); var observation = game.Observe();
        string hash = StateEncoding.Hash(snapshot); rows[1] = "#########";
        foreach (var action in Route().Take(4)) game.Step(action);
        Equal(hash, StateEncoding.Hash(snapshot)); Equal(0L, snapshot.Tick);
        Check(!snapshot.HasKey && !snapshot.DoorOpen && snapshot.LastResult is null, "Old snapshot mutated.");
        Check(observation.Tiles.Any(t => t.Item == ItemKind.Key), "Old observation mutated.");
        var mutable = (IList<Terrain>)scene.Tiles;
        Throws<NotSupportedException>(() => mutable[0] = Terrain.Floor);
    }),
    ("Observe and hash do not mutate game state", () =>
    {
        var game = Game.Create(Load("facility-small.json")); string hash = StateEncoding.Hash(game.Capture());
        for (int i = 0; i < 100; i++) { _ = game.Observe(); _ = StateEncoding.Encode(game.Capture()); }
        Equal(hash, StateEncoding.Hash(game.Capture())); Equal(0L, game.Tick);
    }),
    ("Same scene and actions reproduce hashes and ordered events", () =>
    {
        var a = Game.Create(Load("facility-small.json")); var b = Game.Create(Load("facility-small.json"));
        foreach (var action in Route())
        {
            _ = b.Observe(); _ = b.Observe(); var ar = a.Step(action); var br = b.Step(action);
            Check(ar.Events.SequenceEqual(br.Events), "Event mismatch.");
            Equal(StateEncoding.Hash(a.Capture()), StateEncoding.Hash(b.Capture()));
        }
    }),
    ("Hash covers last-result feedback and rule parameters", () =>
    {
        var a = Game.Create(Load("facility-small.json")); var b = Game.Create(Load("facility-small.json"));
        a.Step(GameAction.Wait()); b.Step(GameAction.Move(Direction.North));
        Check(StateEncoding.Hash(a.Capture()) != StateEncoding.Hash(b.Capture()), "Feedback omitted from hash.");
        Check(StateEncoding.Hash(Game.Create(Load("facility-small.json", 13)).Capture()) !=
            StateEncoding.Hash(Game.Create(Load("facility-small.json", 14)).Capture()), "Budget omitted from hash.");
    }),
    ("Canonical bytes agree with independent Python vectors", () =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests", "Fixtures", "Core", "core-golden.json")));
        var game = Game.Create(Load("facility-small.json"));
        Equal(json.RootElement.GetProperty("initial_sha256").GetString(), StateEncoding.Hash(game.Capture()));
        Equal(json.RootElement.GetProperty("initial_hex").GetString(), Convert.ToHexStringLower(StateEncoding.Encode(game.Capture())));
        foreach (var action in Route()) game.Step(action);
        Equal(json.RootElement.GetProperty("success_sha256").GetString(), StateEncoding.Hash(game.Capture()));
        Equal(json.RootElement.GetProperty("success_hex").GetString(), Convert.ToHexStringLower(StateEncoding.Encode(game.Capture())));
    }),
    ("Structural validation rejects malformed layouts", () =>
    {
        Throws<ArgumentException>(() => new Scenario(["....", "..."], new(0, 0), new(0, 0), new(1, 0), new(2, 0), new(3, 0)));
        Throws<ArgumentException>(() => new Scenario(["..x.."], new(0, 0), new(0, 0), new(1, 0), new(2, 0), new(3, 0)));
        Throws<ArgumentException>(() => new Scenario(["....."], new(0, 0), new(0, 0), new(1, 0), new(1, 0), new(3, 0)));
        Throws<ArgumentException>(() => new Scenario(["#...."], new(0, 0), new(0, 0), new(1, 0), new(2, 0), new(3, 0)));
        Throws<ArgumentOutOfRangeException>(() => Load("facility-small.json", 0));
    }),
    ("Task validation rejects key behind door and door bypass", () =>
    {
        Equal("key_unreachable_before_door", ScenarioValidator.Validate(Load("invalid-key-behind-door.json")).Error);
        Equal("door_not_required", ScenarioValidator.Validate(Load("invalid-door-bypass.json")).Error);
    }),
    ("PCG and named streams match independent Python vectors", () =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests", "Fixtures", "Generation", "rng-golden.json")));
        var pcg = new Pcg32(42, 54);
        foreach (var expected in json.RootElement.GetProperty("reference").EnumerateArray()) Equal(expected.GetUInt32(), pcg.NextUInt32());
        foreach (var vector in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            var stream = NamedRandomStreams.Create(ulong.Parse(vector.GetProperty("seed").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                vector.GetProperty("name").GetString()!);
            foreach (var expected in vector.GetProperty("values").EnumerateArray()) Equal(expected.GetUInt32(), stream.NextUInt32());
        }
    }),
    ("Bounded PCG draws match independent rejection sampling including bound one", () =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests", "Fixtures", "Generation", "rng-golden.json")));
        var pcg = new Pcg32(42, 54);
        int[] bounds = json.RootElement.GetProperty("bounds").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        int[] expected = json.RootElement.GetProperty("bounded").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        for (int i = 0; i < bounds.Length; i++) Equal(expected[i], pcg.NextInt(bounds[i]));
    }),
    ("Random stream contracts reject invalid names and bounds", () =>
    {
        Throws<ArgumentException>(() => NamedRandomStreams.Create(0, "unknown"));
        Throws<ArgumentNullException>(() => NamedRandomStreams.Create(0, null!));
        Throws<ArgumentOutOfRangeException>(() => new Pcg32(0, 0).NextInt(0));
        Throws<ArgumentOutOfRangeException>(() => new Pcg32(0, 0).NextInt(-1));
    }),
    ("Named streams own separate state", () =>
    {
        var map = NamedRandomStreams.Create(42, "map");
        var objects = NamedRandomStreams.Create(42, "objects");
        var copy = NamedRandomStreams.Create(42, "objects");
        for (int i = 0; i < 100; i++) map.NextUInt32();
        for (int i = 0; i < 8; i++) Equal(copy.NextUInt32(), objects.NextUInt32());
        Check(NamedRandomStreams.Create(42, "map").NextUInt32() != NamedRandomStreams.Create(42, "mission").NextUInt32(), "Streams collided.");
    }),
    ("Solver finds shortest full task route using pickup and interact", () =>
    {
        var scene = Load("facility-small.json");
        var solution = ScenarioSolver.Solve(scene);
        Check(solution.Success, solution.Error ?? "No solution."); Equal(13, solution.Actions.Length);
        Equal(2, solution.Actions.Count(a => a.Kind == ActionKind.Pickup));
        Equal(1, solution.Actions.Count(a => a.Kind == ActionKind.Interact));
        var game = Game.Create(scene);
        foreach (var action in solution.Actions) Equal(ActionStatus.Applied, game.Step(action).Outcome.Status);
        Equal(EpisodeEndKind.Success, game.Episode!.Kind);
        Equal("a68fe87640e7105142f481a59e77f43bdb854bab61a826e38ae4fd77ae323262", StateEncoding.Hash(game.Capture()));
    }),
    ("Solver handles final-turn success and insufficient budget", () =>
    {
        Check(ScenarioSolver.Solve(Load("facility-small.json", 13)).Success, "Final turn success lost.");
        var failed = ScenarioSolver.Solve(Load("facility-small.json", 12));
        Check(!failed.Success && failed.Actions.IsEmpty, "Insufficient budget succeeded.");
        Equal("no_solution_within_turn_limit", failed.Error);
    }),
    ("Solver order is deterministic and exploration is bounded", () =>
    {
        var scene = Load("facility-small.json");
        var first = ScenarioSolver.Solve(scene); var second = ScenarioSolver.Solve(scene);
        Check(first.Actions.SequenceEqual(second.Actions), "Search tie order changed.");
        Equal(first.ExpandedStates, second.ExpandedStates);
        Check(first.ExpandedStates <= scene.Width * scene.Height * 8, "Search exceeded state bound.");
    }),
    ("Validator rejects disconnected core and unreachable exit", () =>
    {
        var disconnected = new Scenario(["###########", "#...#.....#", "###########"], new(1, 1), new(1, 1), new(2, 1), new(6, 1), new(8, 1));
        Equal("core_unreachable", ScenarioValidator.Validate(disconnected).Error);
        var exitBehind = new Scenario(["#########", "#.......#", "#########"], new(1, 1), new(7, 1), new(2, 1), new(4, 1), new(6, 1));
        Equal("exit_unreachable_before_door", ScenarioValidator.Validate(exitBehind).Error);
    }),
    ("Validator supports distinct start and exit on the key side", () =>
    {
        var scene = new Scenario(["#########", "#.......#", "#########"], new(3, 1), new(2, 1), new(1, 1), new(4, 1), new(6, 1));
        var validation = ScenarioValidator.Validate(scene); Check(validation.IsValid, "Valid separate exit refused.");
        var game = Game.Create(scene); foreach (var action in validation.Reference!.Actions) game.Step(action);
        Equal(scene.Exit, game.Position); Equal(EpisodeEndKind.Success, game.Episode!.Kind);
    }),
    ("Fixed 100 seeds generate full playable tasks within budget", () =>
    {
        int maxLength = 0, maxAttempts = 0;
        for (ulong seed = 0; seed < 100; seed++)
        {
            GenerationResult result = ScenarioGenerator.Generate(seed);
            Check(result.Success, $"seed {seed}: {result.Error}");
            Check(result.Reference!.Actions.Length <= 128 && result.Attempts <= 16, "Budget exceeded.");
            Check(ScenarioValidator.Validate(result.Scenario!).IsValid, "Task constraints failed.");
            var game = Game.Create(result.Scenario!);
            foreach (var action in result.Reference.Actions) game.Step(action);
            Equal(EpisodeEndKind.Success, game.Episode!.Kind);
            maxLength = Math.Max(maxLength, result.Reference.Actions.Length); maxAttempts = Math.Max(maxAttempts, result.Attempts);
        }
        Console.WriteLine($"  Seeds 0..99: 100/100, max reference {maxLength}, max attempts {maxAttempts}.");
    }),
    ("Generation reproduces hash and route including UInt64 maximum seed", () =>
    {
        foreach (ulong seed in new[] { 0UL, 42UL, ulong.MaxValue })
        {
            var first = ScenarioGenerator.Generate(seed); var second = ScenarioGenerator.Generate(seed);
            Check(first.Success && second.Success, "Generation failed.");
            Equal(StateEncoding.Hash(Game.Create(first.Scenario!).Capture()), StateEncoding.Hash(Game.Create(second.Scenario!).Capture()));
            Check(first.Reference!.Actions.SequenceEqual(second.Reference!.Actions), "Routes differ."); Equal(first.Attempts, second.Attempts);
        }
    }),
    ("Generation exhausts retries without silently changing seed", () =>
    {
        var result = ScenarioGenerator.Generate(42, new(MaxReferenceLength: 1, MaxAttempts: 3));
        Check(!result.Success && result.Scenario is null && result.Reference is null, "Budget bypassed.");
        Equal(42UL, result.Seed); Equal(3, result.Attempts); Equal(ScenarioGenerator.Version, result.Generator);
        Equal("reference_length_exceeded", result.Error);
        var turnLimit = ScenarioGenerator.Generate(42, new(MaxTicks: 1, MaxAttempts: 2));
        Check(!turnLimit.Success, "Turn limit bypassed."); Equal(2, turnLimit.Attempts);
        Equal("no_solution_within_turn_limit", turnLimit.Error);
    }),
    ("Generator enforces finite retry and dimension bounds", () =>
    {
        foreach (GenerationOptions options in new[] { new GenerationOptions(Width: 8), new(Height: 6), new(MaxAttempts: 0), new(MaxAttempts: 65),
            new(MaxTicks: 0), new(MaxReferenceLength: 0), new(VisibilityRadius: 129) })
            Throws<ArgumentOutOfRangeException>(() => ScenarioGenerator.Generate(0, options));
        Check(ScenarioGenerator.Generate(42, new(Width: 9, Height: 7)).Success, "Minimum map failed.");
    })
};
int failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
Console.WriteLine($"Core: {tests.Length - failed}/{tests.Length} passed.");
int m7Failed = await M7MultiSeatChecks.RunAsync(Root());
int m7GenFailed = await M7MultiGenerationChecks.RunAsync(Root());
int m7EdgeFailed = await M7MultiSeatEdgeChecks.RunAsync(Root());
int m7StreamFailed = await M7StreamEquivalenceChecks.RunAsync(Root());
return failed == 0 && m7Failed == 0 && m7GenFailed == 0 && m7EdgeFailed == 0 && m7StreamFailed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void Throws<T>(Action operation) where T : Exception
{
    try { operation(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static string Root()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "docs", "parallel-work.md"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new Exception("Cannot locate fixture root.");
}
static Scenario Load(string file, int? maxTicks = null)
{
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests", "Fixtures", "Core", file)));
    var root = json.RootElement;
    Position P(string field) => new(root.GetProperty(field).GetProperty("x").GetInt32(), root.GetProperty(field).GetProperty("y").GetInt32());
    return new Scenario(root.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToArray(),
        P("start"), P("exit"), P("key"), P("door"), P("core"), maxTicks ?? root.GetProperty("max_ticks").GetInt32(),
        root.GetProperty("visibility_radius").GetInt32());
}
static ImmutableArray<GameAction> Route()
{
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "tests", "Fixtures", "Core", "facility-small.actions.json")));
    return json.RootElement.EnumerateArray().Select(a => a.GetProperty("type").GetString() switch
    {
        "move" => GameAction.Move(Enum.Parse<Direction>(a.GetProperty("direction").GetString()!, true)),
        "interact" => GameAction.Interact(Enum.Parse<Direction>(a.GetProperty("direction").GetString()!, true)),
        "pickup" => GameAction.Pickup(),
        _ => throw new Exception("Unsupported fixture action.")
    }).ToImmutableArray();
}
