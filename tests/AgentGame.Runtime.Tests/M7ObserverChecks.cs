using AgentGame.Core;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;
using AgentGame.Runtime.Observer;

internal static class M7ObserverChecks
{
    public static int Run(string root)
    {
        _ = root;
        var checks = new (string Name, Action Run)[]
        {
            ("M7 initial seat views stay isolated from the full spectator map", () =>
            {
                var (game, session) = Create();
                Equal("d9e985318403061163239a6e14178acb4a2d3bbec084e994f83c66842ceb6ac5", MultiStateEncoding.Hash(game.Capture()));
                foreach (int seat in new[] { 0, 1 }) CheckView(game, session, seat);
                var spectator = session.GetHub("spectator").CurrentSnapshot();
                Equal(27, spectator.State.Tiles.Length); Equal(2, spectator.State.Entities.Count(e => e.Kind == "seat"));
                Require(spectator.State.Tiles.All(t => t.LastSeenTick is null), "Spectator memory is incomplete.");
                Require(!session.GetHub("agent:0").CurrentSnapshot().State.Entities.Any(e => e.Id == "core"), "Closed-door information leaked.");
                Reject(() => session.GetHub("agent:2"));
            }),
            ("M7 every cooperative turn reconstructs all views from independent batches", () =>
            {
                var (game, session) = Create();
                var states = session.Views.ToDictionary(v => v, v => MultiVisualState.FromSnapshot(session.GetHub(v).CurrentSnapshot()));
                foreach (GameAction action in Route())
                {
                    Advance(action); if (game.Episode is null) Advance(GameAction.Wait());
                }
                Equal(EpisodeEndKind.Success, game.Episode!.Kind);
                Equal(0, game.Capture().CoreHolder); Equal(25L, game.Tick);
                void Advance(GameAction action)
                {
                    var step = game.Step(game.OwnerSeat, action);
                    var pending = session.PrepareStep(game.Capture(), Observations(game), step);
                    foreach (MultiStepBatchMessage batch in pending.Batches) states[batch.View].Apply(batch);
                    session.Commit(pending);
                    foreach (var view in session.Views) Equal(Wire(session.GetHub(view).CurrentSnapshot()), Wire(states[view].Snapshot()));
                    CheckView(game, session, 0); CheckView(game, session, 1);
                }
            }),
            ("M7 another seat's private action events do not leak into a seat view", () =>
            {
                var (game, session) = Create();
                Commit(game, session, GameAction.Wait());
                var step = game.Step(1, GameAction.Move(Direction.West));
                var pending = session.PrepareStep(game.Capture(), Observations(game), step);
                var privateView = Batch(pending, "agent:0");
                Require(privateView.Events.Length == 0, "Another seat's move event leaked.");
                Equal("seat:1", Batch(pending, "spectator").Events.Single(e => e.Type == ObserverEventTypeDto.Moved).EntityId);
                Equal("seat:1", Batch(pending, "agent:1").Events.Single(e => e.Type == ObserverEventTypeDto.Moved).EntityId);
                session.Commit(pending);
            }),
            ("M7 stages can be discarded without advancing committed memory or sequence", () =>
            {
                var (game, session) = Create();
                var before = session.Views.ToDictionary(v => v, v => Wire(session.GetHub(v).CurrentSnapshot()));
                var step = game.Step(0, GameAction.Move(Direction.East));
                var first = session.PrepareStep(game.Capture(), Observations(game), step);
                foreach (var v in session.Views) Equal(before[v], Wire(session.GetHub(v).CurrentSnapshot()));
                var second = session.PrepareStep(game.Capture(), Observations(game), step);
                var callerCopy = Batch(second, "agent:0");
                callerCopy.Patch.EntityUpserts[0] = callerCopy.Patch.EntityUpserts[0] with { X = 999 };
                session.Commit(second);
                Equal(1L, session.GetHub("agent:0").CurrentSnapshot().Tick);
                Reject(() => session.Commit(first)); Reject(() => session.Commit(second));
                var (_, foreign) = Create(); Reject(() => foreign.Commit(first));
            }),
            ("M7 status sequence numbers advance independently for each view", () =>
            {
                var (game, session) = Create();
                session.Commit(session.PrepareStatus("agent:0", AgentStatusDto.Waiting));
                Equal(1L, session.GetHub("agent:0").CurrentSnapshot().BaseSeq);
                Equal(0L, session.GetHub("agent:1").CurrentSnapshot().BaseSeq);
                Equal(0L, session.GetHub("spectator").CurrentSnapshot().Tick);
                Commit(game, session, GameAction.Wait());
                Equal(2L, session.GetHub("agent:0").CurrentSnapshot().BaseSeq);
                Equal(1L, session.GetHub("agent:1").CurrentSnapshot().BaseSeq);
                Equal(1L, session.GetHub("spectator").CurrentSnapshot().BaseSeq);
            }),
            ("M7 a slow seat subscriber detaches while other views remain continuous", () =>
            {
                var (game, session) = Create();
                using var slow = session.GetHub("agent:0").Register(1).Subscription;
                var registration = session.GetHub("agent:1").Register(8);
                using var fast = registration.Subscription;
                var state = MultiVisualState.FromSnapshot(registration.Snapshot);
                Commit(game, session, GameAction.Wait()); Commit(game, session, GameAction.Wait());
                Require(slow.RequiresResync && !fast.RequiresResync, "Overflow affected another view.");
                while (fast.Reader.TryRead(out var message)) state.Apply(message);
                Equal(Wire(session.GetHub("agent:1").CurrentSnapshot()), Wire(state.Snapshot()));
                Require(slow.Reader.TryRead(out var old) && old is MultiStepBatchMessage { Seq: 1 }, "Overflow overwrote the retained prefix.");
                Reject(() => slow.Reader.Completion.GetAwaiter().GetResult());
                var reset = session.GetHub("agent:0").Register(2);
                using var fresh = reset.Subscription;
                Commit(game, session, GameAction.Wait());
                Require(fresh.Reader.TryRead(out var next) && next is MultiStepBatchMessage batch && batch.Seq == reset.Snapshot.BaseSeq + 1, "Resubscription missed its next sequence.");
            }),
            ("M7 stale core memory clears when a different seat becomes its carrier", CoreMemory),
            ("M7 packets and snapshots have independent ownership across subscribers", () =>
            {
                var (game, session) = Create(); var hub = session.GetHub("agent:0");
                using var left = hub.Register(4).Subscription; using var right = hub.Register(4).Subscription;
                Commit(game, session, GameAction.Move(Direction.East));
                Require(left.Reader.TryRead(out var a), "Missing left committed packet.");
                Require(right.Reader.TryRead(out var b), "Missing right committed packet.");
                var leftBatch = (MultiStepBatchMessage)a!; var rightBatch = (MultiStepBatchMessage)b!;
                string expected = MultiObserverCodec.Encode(rightBatch), snapshot = Wire(hub.CurrentSnapshot());
                leftBatch.Patch.EntityUpserts[0] = leftBatch.Patch.EntityUpserts[0] with { X = 999 };
                leftBatch.Events[0] = leftBatch.Events[0] with { EntityId = "seat:3" };
                var copy = hub.CurrentSnapshot(); copy.State.Entities[0] = copy.State.Entities[0] with { X = 999 };
                Equal(expected, MultiObserverCodec.Encode(rightBatch)); Equal(snapshot, Wire(hub.CurrentSnapshot()));
            }),
            ("M7 reducer rejects stream mismatches and foreign entities transactionally", () =>
            {
                var (game, session) = Create();
                var reducer = MultiVisualState.FromSnapshot(session.GetHub("agent:0").CurrentSnapshot());
                string before = Wire(reducer.Snapshot());
                var step = game.Step(0, GameAction.Move(Direction.East));
                var pending = session.PrepareStep(game.Capture(), Observations(game), step); var valid = Batch(pending, "agent:0");
                object[] invalid = [valid with { View = "agent:1" }, valid with { Seq = 2 }, valid with { Tick = 0 },
                    valid with { Protocol = "observer/1" }, new StepBatchMessage(),
                    valid with { Patch = valid.Patch with { EntityUpserts = [new() { Id = "seat:1", Kind = "seat", X = 1, Y = 1 }] } },
                    valid with { Patch = valid.Patch with { EntityRemovals = ["seat:0"], EntityUpserts = [new() { Id = "seat:0", Kind = "seat", X = 2, Y = 1 }] } }];
                foreach (var packet in invalid) { Reject(() => reducer.Apply(packet)); Equal(before, Wire(reducer.Snapshot())); }
                reducer.Apply(valid); Equal(1L, reducer.Snapshot().Tick);
            }),
            ("M7 incomplete or mismatched observations cannot dirty committed state", () =>
            {
                var (game, session) = Create(); string before = Wire(session.GetHub("agent:0").CurrentSnapshot());
                var step = game.Step(0, GameAction.Wait()); var observed = Observations(game);
                Reject(() => session.PrepareStep(game.Capture(), [observed[0]], step));
                Reject(() => session.PrepareStep(game.Capture(), [observed[0], observed[0]], step));
                Reject(() => session.PrepareStep(game.Capture(), [observed[0] with { Tick = 0 }, observed[1]], step));
                Equal(before, Wire(session.GetHub("agent:0").CurrentSnapshot()));
                session.Commit(session.PrepareStep(game.Capture(), observed, step));
            }),
            ("M7 wire privacy rejects malformed seat identities and foreign event actors", () =>
            {
                var (game, session) = Create(); var initial = session.GetHub("agent:0").CurrentSnapshot();
                foreach (string id in new[] { "seat:2147483648", "seat:-1", "seat:00", "seat:4" })
                    Reject(() => MultiObserverCodec.Encode(initial with { State = initial.State with
                    { Entities = [new() { Id = id, Kind = "seat", X = 1, Y = 1 }] } }));
                Reject(() => MultiObserverCodec.Encode(initial with { State = initial.State with
                { Entities = [new() { Id = "hidden-teammate", Kind = "seat", X = 1, Y = 1 }] } }));
                var step = game.Step(0, GameAction.Wait());
                var batch = Batch(session.PrepareStep(game.Capture(), Observations(game), step), "agent:0");
                Reject(() => MultiObserverCodec.Encode(batch with { Events = [new() { Type = ObserverEventTypeDto.Moved, EntityId = "seat:1" }] }));
                Reject(() => MultiObserverCodec.Encode(batch with { Events = [new() { Type = ObserverEventTypeDto.Moved, EntityId = "seat:2147483648" }] }));
            }),
            ("M7 one through four seats maintain separate views even at overlapping spawns", () =>
            {
                for (int count = 1; count <= 4; count++)
                {
                    var scenario = new MultiScenario(["#########", "#.......#", "#########"],
                        Enumerable.Range(0, count).Select(s => new Position(s % 2 == 0 ? 1 : 7, 1)),
                        new(1, 1), new(2, 1), new(4, 1), new(6, 1));
                    var game = MultiGame.Create(scenario); var session = new MultiObserverSession(game.Capture(), Observations(game), "seat-count");
                    Equal(count + 1, session.Views.Length);
                    for (int turn = 0; turn < count * 2; turn++) Commit(game, session, GameAction.Wait());
                    for (int seat = 0; seat < count; seat++) CheckView(game, session, seat);
                    Equal(count, session.GetHub("spectator").CurrentSnapshot().State.Entities.Count(e => e.Kind == "seat"));
                }
            }),
            ("M7 concurrent registration never misses its first committed envelope", () =>
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var (game, session) = Create(); var hub = session.GetHub("agent:0");
                    var step = game.Step(0, GameAction.Wait()); var pending = session.PrepareStep(game.Capture(), Observations(game), step);
                    using var signal = new ManualResetEventSlim();
                    var register = Task.Run(() => { signal.Wait(); return hub.Register(2); });
                    var publish = Task.Run(() => { signal.Wait(); session.Commit(pending); });
                    signal.Set(); Task.WhenAll(register, publish).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    var registration = register.Result; using var sub = registration.Subscription;
                    if (registration.Snapshot.BaseSeq == 0)
                        Require(sub.Reader.TryRead(out var message) && message is MultiStepBatchMessage { Seq: 1 }, "Registration lost its first commit.");
                    else { Equal(1L, registration.Snapshot.BaseSeq); Require(!sub.Reader.TryRead(out _), "Snapshot covered a duplicated commit."); }
                }
            }),
            ("M7 a mid-run snapshot resumes immediately at base_seq plus one", () =>
            {
                var (game, session) = Create(); Commit(game, session, GameAction.Move(Direction.East));
                var reset = session.GetHub("spectator").Register(8); using var sub = reset.Subscription;
                var state = MultiVisualState.FromSnapshot(reset.Snapshot);
                Commit(game, session, GameAction.Wait()); Commit(game, session, GameAction.Pickup());
                while (sub.Reader.TryRead(out var message)) state.Apply(message);
                Equal(Wire(session.GetHub("spectator").CurrentSnapshot()), Wire(state.Snapshot()));
                session.Complete(); Reject(() => session.GetHub("agent:0").Register());
                Reject(() => session.PrepareStatus("agent:0", AgentStatusDto.Stopped));
            })
        };
        int failed = 0;
        foreach (var (name, run) in checks)
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        Console.WriteLine($"M7 Observer: {checks.Length - failed}/{checks.Length} passed.");
        return failed;
    }

    private static void CoreMemory()
    {
        string wall = new('#', 23);
        var scenario = new MultiScenario([wall, "#.....................#", wall], [new(1, 1), new(16, 1)],
            new(1, 1), new(2, 1), new(10, 1), new(15, 1), visibilityRadius: 1);
        var game = MultiGame.Create(scenario); var session = new MultiObserverSession(game.Capture(), Observations(game), "memory");
        var actions = new List<GameAction> { GameAction.Move(Direction.East), GameAction.Pickup() };
        actions.AddRange(Enumerable.Repeat(GameAction.Move(Direction.East), 7));
        actions.Add(GameAction.Interact(Direction.East)); actions.AddRange(Enumerable.Repeat(GameAction.Move(Direction.East), 6));
        actions.Add(GameAction.Pickup());
        int turns = 0;
        foreach (var action in actions)
        {
            Commit(game, session, action);
            if (turns == 3)
            {
                var remembered = session.GetHub("agent:1").CurrentSnapshot().State.Tiles.Single(t => t.X == 15 && t.Y == 1);
                Equal(ItemKindDto.Core, remembered.Item); Equal(5L, remembered.LastSeenTick);
            }
            Commit(game, session, turns++ < 3 ? GameAction.Move(Direction.West) : GameAction.Wait());
        }
        var state = session.GetHub("agent:1").CurrentSnapshot().State;
        Require(state.Tiles.Single(t => t.X == 15 && t.Y == 1).Item is null && state.Entities.All(e => e.Id != "core"), "Collected core remained in other seat's memory.");
        Require(!state.Inventory.Contains(ItemKindDto.Core) && !game.Observe(1).HasCore, "Other seat inherited the carrier's inventory.");
        Require(session.GetHub("agent:0").CurrentSnapshot().State.Inventory.Contains(ItemKindDto.Core), "Carrier inventory omitted core.");
        Commit(game, session, GameAction.Wait()); Commit(game, session, GameAction.Move(Direction.East));
        var rediscovered = session.GetHub("agent:1").CurrentSnapshot().State.Tiles.Single(t => t.X == 15 && t.Y == 1);
        Require(rediscovered.LastSeenTick is null && rediscovered.Item is null, "Rediscovery retained stale item or visibility timestamp.");
    }
    private static (MultiGame Game, MultiObserverSession Session) Create()
    {
        var game = MultiGame.Create(new MultiScenario(["#########", "#.......#", "#########"], [new(1, 1), new(7, 1)],
            new(1, 1), new(2, 1), new(4, 1), new(6, 1)));
        return (game, new(game.Capture(), Observations(game), "m7-observer"));
    }
    private static MultiObservation[] Observations(MultiGame game) => Enumerable.Range(0, game.Positions.Length).Select(game.Observe).ToArray();
    private static void Commit(MultiGame game, MultiObserverSession session, GameAction action)
    {
        var step = game.Step(game.OwnerSeat, action);
        session.Commit(session.PrepareStep(game.Capture(), Observations(game), step));
    }
    private static MultiStepBatchMessage Batch(MultiObserverSession.PreparedCommit pending, string view) =>
        pending.Batches.OfType<MultiStepBatchMessage>().Single(b => b.View == view);
    private static void CheckView(MultiGame game, MultiObserverSession session, int seat)
    {
        var observation = game.Observe(seat); var state = session.GetHub($"agent:{seat}").CurrentSnapshot().State;
        Equal(string.Join(';', observation.Tiles.Select(t => t.Position)), string.Join(';', state.Tiles.Where(t => t.LastSeenTick is null).Select(t => new Position(t.X, t.Y))));
        Require(state.Entities.Where(e => e.Kind == "seat").All(e => e.Id == $"seat:{seat}"), "Another seat entity leaked.");
        var self = state.Entities.Single(e => e.Id == $"seat:{seat}"); Equal(observation.Position, new(self.X, self.Y));
        Equal(observation.HasKey, state.Inventory.Contains(ItemKindDto.Key)); Equal(observation.HasCore, state.Inventory.Contains(ItemKindDto.Core));
    }
    private static GameAction[] Route() => [GameAction.Move(Direction.East), GameAction.Pickup(), GameAction.Move(Direction.East),
        GameAction.Interact(Direction.East), GameAction.Move(Direction.East), GameAction.Move(Direction.East), GameAction.Move(Direction.East),
        GameAction.Pickup(), GameAction.Move(Direction.West), GameAction.Move(Direction.West), GameAction.Move(Direction.West), GameAction.Move(Direction.West), GameAction.Move(Direction.West)];
    private static string Wire(MultiSnapshotMessage snapshot) => MultiObserverCodec.Encode(snapshot);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Reject(Action action) { try { action(); } catch (Exception error) when (error is ProtocolException or ArgumentException or InvalidOperationException or ObserverResyncException) { return; } throw new Exception("Invalid operation accepted."); }
}
