using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Observer;

internal static class M4ObserverChecks
{
    public static int Run(string root)
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Observer patches reconstruct every real manual commit and reset point", () =>
            {
                Game game = Manual(root);
                var projection = new ObserverProjection(game.Capture(), game.Observe());
                VisualState reducer = VisualState.FromSnapshot(Snapshot(projection.State));
                long seq = 0;
                foreach (GameAction action in Route())
                {
                    reducer.Apply(Status(++seq, game.Tick));
                    Equal(game.Tick, reducer.Snapshot().Tick);
                    StepResult step = game.Step(action);
                    ObserverPatch patch = projection.Apply(game.Observe());
                    reducer.Apply(Batch(++seq, step, patch));
                    Equal(Json(projection.State), Json(reducer.Snapshot().State));
                    if (game.Tick % 3 == 0) reducer = VisualState.FromSnapshot(reducer.Snapshot());
                }
                Equal(13L, reducer.Snapshot().Tick);
                Equal(EpisodeKindDto.Success, reducer.Snapshot().State.Episode!.Kind);
            }),
            ("Observer item pickup explicitly clears tiles and removes stable entities", () =>
            {
                Game game = Manual(root);
                var projection = new ObserverProjection(game.Capture(), game.Observe());
                game.Step(GameAction.Move(Direction.East)); projection.Apply(game.Observe());
                StepResult picked = game.Step(GameAction.Pickup());
                ObserverPatch patch = projection.Apply(game.Observe());
                Check(patch.EntityRemovals.SequenceEqual(["key"]), "Key entity was not explicitly removed.");
                Equal<ItemKindDto?>(null, patch.TileUpserts.Single(tile => tile.X == 2 && tile.Y == 1).Item);
                Check(patch.Inventory.SequenceEqual([ItemKindDto.Key]), "Pickup inventory differs.");
                ObserverEventDto[] events = ObserverProjection.Events(picked);
                Equal(ObserverEventTypeDto.PickedUp, events[0].Type); Equal("key", events[0].EntityId);
                Equal(MissionPhaseDto.OpenDoor, events[1].Phase);
                game.Step(GameAction.Move(Direction.East)); projection.Apply(game.Observe());
                StepResult opened = game.Step(GameAction.Interact(Direction.East));
                patch = projection.Apply(game.Observe());
                Equal("open", patch.EntityUpserts.Single(entity => entity.Id == "door").State);
                Equal(ObserverEventTypeDto.DoorOpened, ObserverProjection.Events(opened)[0].Type);
            }),
            ("Observer display memory retains the last actual observed tick until rediscovery", () =>
            {
                Game game = Game.Create(new Scenario(["#########", "#.......#", "#########"],
                    new(1, 1), new(1, 1), new(2, 1), new(4, 1), new(6, 1), visibilityRadius: 1));
                var projection = new ObserverProjection(game.Capture(), game.Observe());
                Equal(1, projection.State.VisibleRadius);
                Check(!projection.State.Entities.Any(entity => entity.Id is "core" or "door"), "Hidden layout leaked at reset.");
                game.Step(GameAction.Move(Direction.East)); ObserverPatch departure = projection.Apply(game.Observe());
                Equal(0L, departure.TileUpserts.Single(tile => tile.X == 0 && tile.Y == 1).LastSeenTick);
                game.Step(GameAction.Wait()); ObserverPatch wait = projection.Apply(game.Observe());
                Check(!wait.TileUpserts.Any(tile => tile.X == 0 && tile.Y == 1), "Stale memory was restamped.");
                Equal(0L, projection.State.Tiles.Single(tile => tile.X == 0 && tile.Y == 1).LastSeenTick);
                game.Step(GameAction.Move(Direction.West)); ObserverPatch returned = projection.Apply(game.Observe());
                Equal<long?>(null, returned.TileUpserts.Single(tile => tile.X == 0 && tile.Y == 1).LastSeenTick);
            }),
            ("Observer projection and reducer own all published mutable DTO arrays", () =>
            {
                Game game = Manual(root);
                var projection = new ObserverProjection(game.Capture(), game.Observe());
                string before = Json(projection.State);
                ObserverState exposed = projection.State;
                exposed.Tiles[0] = exposed.Tiles[0] with { X = 100 };
                exposed.Entities[0] = exposed.Entities[0] with { Id = "changed" };
                Equal(before, Json(projection.State));
                SnapshotMessage input = Snapshot(projection.State);
                VisualState reducer = VisualState.FromSnapshot(input);
                input.State.Tiles[0] = input.State.Tiles[0] with { X = 101 };
                Equal(before, Json(reducer.Snapshot().State));
                StepResult step = game.Step(GameAction.Move(Direction.East));
                ObserverPatch patch = projection.Apply(game.Observe());
                reducer.Apply(Batch(1, step, patch));
                before = Json(reducer.Snapshot().State);
                patch.EntityUpserts[0] = patch.EntityUpserts[0] with { X = 102 };
                SnapshotMessage output = reducer.Snapshot();
                output.State.Tiles[0] = output.State.Tiles[0] with { X = 103 };
                Equal(before, Json(reducer.Snapshot().State));
                Equal(Json(projection.State), before);
            }),
            ("Observer reducer rejects identity changes gaps duplicates and unsafe sequence values transactionally", () =>
            {
                var reducer = VisualState.FromSnapshot(Snapshot(new ObserverProjection(Manual(root).Capture(), Manual(root).Observe()).State));
                string before = ObserverCodec.Encode(reducer.Snapshot());
                object[] invalid =
                [
                    Status(2, 0), Status(0, 0), Status(-1, 0), Status(9_007_199_254_740_992, 0),
                    Status(1, 1), Status(1, 0) with { RunId = "other" },
                    Status(1, 0) with { View = "debug" }, Status(1, 0) with { Protocol = "observer/2" }
                ];
                foreach (object envelope in invalid)
                {
                    Throws<ProtocolException>(() => reducer.Apply(envelope));
                    Equal(before, ObserverCodec.Encode(reducer.Snapshot()));
                }
                reducer.Apply(Status(1, 0)); Equal(1L, reducer.Snapshot().BaseSeq); Equal(0L, reducer.Snapshot().Tick);
                Throws<ProtocolException>(() => reducer.Apply(Snapshot(reducer.Snapshot().State)));
            }),
            ("Observer reducer rejects duplicate conflicting unknown and inconsistent patch coordinates transactionally", () =>
            {
                Game game = Manual(root);
                var projection = new ObserverProjection(game.Capture(), game.Observe());
                var reducer = VisualState.FromSnapshot(Snapshot(projection.State));
                StepResult step = game.Step(GameAction.Wait());
                ObserverPatch patch = projection.Apply(game.Observe());
                ObserverTile current = projection.State.Tiles[0];
                ObserverEntity player = projection.State.Entities.Single(entity => entity.Id == "player");
                ObserverPatch[] invalid =
                [
                    patch with { TileUpserts = [current, current] },
                    patch with { EntityUpserts = [player, player] },
                    patch with { EntityUpserts = [player], EntityRemovals = ["player"] },
                    patch with { EntityRemovals = ["missing"] },
                    patch with { VisibleTiles = [.. patch.VisibleTiles, patch.VisibleTiles[0]] },
                    patch with { VisibleTiles = [new() { X = 127, Y = 127 }] },
                    patch with { VisibleTiles = [] },
                    patch with { TileUpserts = [current with { LastSeenTick = 1 }] }
                ];
                string before = ObserverCodec.Encode(reducer.Snapshot());
                foreach (ObserverPatch malformed in invalid)
                {
                    Throws<ProtocolException>(() => reducer.Apply(Batch(1, step, malformed)));
                    Equal(before, ObserverCodec.Encode(reducer.Snapshot()));
                }
                reducer.Apply(Batch(1, step, patch)); Equal(Json(projection.State), Json(reducer.Snapshot().State));
            }),
            ("Observer snapshots reject duplicate coordinates unknown entity tiles and invalid history", () =>
            {
                Game game = Manual(root);
                ObserverState state = new ObserverProjection(game.Capture(), game.Observe()).State;
                Throws<ProtocolException>(() => VisualState.FromSnapshot(Snapshot(state with { Tiles = [.. state.Tiles, state.Tiles[0]] })));
                Throws<ProtocolException>(() => VisualState.FromSnapshot(Snapshot(state with
                { Entities = [.. state.Entities, new() { Id = "stray", Kind = "key", X = 127, Y = 127 }] })));
                Throws<ProtocolException>(() => VisualState.FromSnapshot(Snapshot(state with
                { Tiles = state.Tiles.Select((tile, index) => index == 0 ? tile with { LastSeenTick = 1 } : tile).ToArray() })));
            }),
            ("Observer reducer accepts same-tick historical memory from the frozen wire fixture", () =>
            {
                string fixture = File.ReadLines(Path.Combine(root, "tests", "Fixtures", "Protocol", "observer", "valid-observer.jsonl")).First();
                var snapshot = (SnapshotMessage)ObserverCodec.Parse(fixture);
                Equal(42L, snapshot.Tick);
                var reducer = VisualState.FromSnapshot(snapshot);
                Equal(42L, reducer.Snapshot().State.Tiles.Single(tile => tile.X == 2 && tile.Y == 1).LastSeenTick);
                reducer.Apply(new AgentStatusMessage
                {
                    RunId = snapshot.RunId, View = snapshot.View, Seq = snapshot.BaseSeq + 1,
                    Tick = snapshot.Tick, AgentStatus = AgentStatusDto.Stopped
                });
                Equal(42L, reducer.Snapshot().State.Tiles.Single(tile => tile.X == 2 && tile.Y == 1).LastSeenTick);
                Equal(snapshot.Tick, reducer.Snapshot().Tick);
            }),
            ("Observer reducer accepts the frozen wire fixture's whole stream", () =>
            {
                // The frozen fixture is a hand-written shape example, but it must still be a
                // stream a real viewer can apply: the snapshot sets the baseline and the single
                // step_batch follows at base_seq + 1 without removing unknown entities.
                string[] lines = File.ReadAllLines(Path.Combine(root, "tests", "Fixtures", "Protocol", "observer", "valid-observer.jsonl"));
                Equal(2, lines.Length);
                var snapshot = (SnapshotMessage)ObserverCodec.Parse(lines[0]);
                var batch = (StepBatchMessage)ObserverCodec.Parse(lines[1]);
                Equal(snapshot.BaseSeq + 1, batch.Seq);
                var reducer = VisualState.FromSnapshot(snapshot);
                reducer.Apply(batch);
                Equal(batch.Seq, reducer.Snapshot().BaseSeq);
                Equal(batch.Tick, reducer.Snapshot().Tick);
                Equal(MissionPhaseDto.FindCore, reducer.Snapshot().State.MissionPhase);
            }),
            ("Observer projection validates ordering and maps blocked-move event destination", () =>
            {
                Game game = Manual(root);
                var projection = new ObserverProjection(game.Capture(), game.Observe());
                Throws<ArgumentException>(() => projection.Apply(game.Observe()));
                StepResult step = game.Step(GameAction.Move(Direction.North));
                ObserverEventDto item = ObserverProjection.Events(step)[0];
                Equal(ObserverEventTypeDto.MoveBlocked, item.Type); Equal(1, item.Position!.X); Equal(0, item.Position.Y);
                Equal("wall", item.Reason); Equal("player", item.EntityId);
                projection.Apply(game.Observe());
                Equal(Json(projection.State), Json(VisualState.FromSnapshot(Snapshot(projection.State) with { Tick = 1 }).Snapshot().State));
            })
        };
        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M4 Observer: {tests.Length - failed}/{tests.Length} passed.");
        return failed;
    }

    private static Game Manual(string root) => Game.Create(ScenarioService.ToCoreScenario(ScenarioService.Read(
        Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json"))));
    private static GameAction[] Route() =>
    [
        GameAction.Move(Direction.East), GameAction.Pickup(), GameAction.Move(Direction.East), GameAction.Interact(Direction.East),
        GameAction.Move(Direction.East), GameAction.Move(Direction.East), GameAction.Move(Direction.East), GameAction.Pickup(),
        GameAction.Move(Direction.West), GameAction.Move(Direction.West), GameAction.Move(Direction.West),
        GameAction.Move(Direction.West), GameAction.Move(Direction.West)
    ];
    private static SnapshotMessage Snapshot(ObserverState state) => new() { RunId = "observer-tests", State = state, AgentStatus = AgentStatusDto.Waiting };
    private static AgentStatusMessage Status(long seq, long tick) => new() { RunId = "observer-tests", Seq = seq, Tick = tick, AgentStatus = AgentStatusDto.Waiting };
    private static StepBatchMessage Batch(long seq, StepResult step, ObserverPatch patch) => new()
    { RunId = "observer-tests", Seq = seq, Tick = step.Tick, AgentStatus = AgentStatusDto.ActionReceived, Events = ObserverProjection.Events(step), Patch = patch };
    private static string Json(ObserverState state) => ProtocolJson.EncodeLine(state);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
    }
}
