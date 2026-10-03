using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Observer;

internal static class M4HubChecks
{
    public static int Run(string root)
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Observer capacity-one overflow retains the first envelope and requests resync", () =>
            {
                var fixture = Create(root);
                using ObserverSubscription subscriber = fixture.Hub.Register(1).Subscription;
                StepBatchMessage first = Advance(fixture);
                Publish(fixture.Hub, first);
                Publish(fixture.Hub, Status(2, 1));
                Check(subscriber.RequiresResync, "Overflow did not request resynchronization.");
                Check(subscriber.Reader.TryRead(out object? received), "Overflow lost the retained first envelope.");
                Equal(1L, ((StepBatchMessage)received!).Seq);
                Check(!subscriber.Reader.TryRead(out _), "Overflow replaced or appended an invalid second envelope.");
                Throws<ObserverResyncException>(() => subscriber.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
                Equal(2L, fixture.Hub.CurrentSnapshot().BaseSeq);
                Equal(1L, fixture.Hub.CurrentSnapshot().Tick);
            }),
            ("Observer re-registration resumes immediately after its authoritative snapshot", () =>
            {
                var fixture = Create(root);
                using ObserverSubscription slow = fixture.Hub.Register(1).Subscription;
                Publish(fixture.Hub, Advance(fixture)); Publish(fixture.Hub, Status(2, 1));
                ObserverRegistration resumed = fixture.Hub.Register(2);
                using ObserverSubscription active = resumed.Subscription;
                Equal(2L, resumed.Snapshot.BaseSeq);
                Check(!active.Reader.TryRead(out _), "Registration replayed an envelope covered by its snapshot.");
                var reducer = VisualState.FromSnapshot(resumed.Snapshot);
                Publish(fixture.Hub, Status(3, 1));
                Check(active.Reader.TryRead(out object? received), "Resumed observer lost the next envelope.");
                Equal(3L, ((AgentStatusMessage)received!).Seq);
                reducer.Apply(received!);
                Equal(ObserverCodec.Encode(fixture.Hub.CurrentSnapshot()), ObserverCodec.Encode(reducer.Snapshot()));
                Check(slow.RequiresResync, "Detached subscriber was silently revived.");
            }),
            ("Observer subscriber envelopes and snapshots have independent mutable array ownership", () =>
            {
                var fixture = Create(root);
                ObserverRegistration left = fixture.Hub.Register(2), right = fixture.Hub.Register(2);
                using ObserverSubscription leftSubscription = left.Subscription;
                using ObserverSubscription rightSubscription = right.Subscription;
                string initial = ObserverCodec.Encode(fixture.Hub.CurrentSnapshot());
                left.Snapshot.State.Tiles[0] = left.Snapshot.State.Tiles[0] with { X = 127 };
                Equal(initial, ObserverCodec.Encode(right.Snapshot));
                Equal(initial, ObserverCodec.Encode(fixture.Hub.CurrentSnapshot()));
                StepBatchMessage source = Advance(fixture);
                Publish(fixture.Hub, source);
                Check(leftSubscription.Reader.TryRead(out object? leftEnvelope), "Left observer missed publication.");
                Check(rightSubscription.Reader.TryRead(out object? rightEnvelope), "Right observer missed publication.");
                var leftBatch = (StepBatchMessage)leftEnvelope!;
                var rightBatch = (StepBatchMessage)rightEnvelope!;
                string rightBefore = ObserverCodec.Encode(rightBatch), hubBefore = ObserverCodec.Encode(fixture.Hub.CurrentSnapshot());
                leftBatch.Patch.EntityUpserts[0] = leftBatch.Patch.EntityUpserts[0] with { X = 126 };
                leftBatch.Patch.VisibleTiles[0] = new() { X = 125, Y = 125 };
                source.Patch.EntityUpserts[0] = source.Patch.EntityUpserts[0] with { X = 124 };
                Equal(rightBefore, ObserverCodec.Encode(rightBatch));
                Equal(hubBefore, ObserverCodec.Encode(fixture.Hub.CurrentSnapshot()));
                SnapshotMessage fresh = fixture.Hub.CurrentSnapshot();
                fresh.State.Tiles[0] = fresh.State.Tiles[0] with { X = 123 };
                Equal(hubBefore, ObserverCodec.Encode(fixture.Hub.CurrentSnapshot()));
            }),
            ("Observer registration racing a commit never loses or repeats the first sequence", () =>
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var fixture = Create(root);
                    ObserverHub.Prepared publication = fixture.Hub.Prepare(Advance(fixture));
                    using var start = new ManualResetEventSlim(false);
                    Task<ObserverRegistration> registering = Task.Run(() => { start.Wait(); return fixture.Hub.Register(3); });
                    Task publishing = Task.Run(() => { start.Wait(); fixture.Hub.Publish(publication); });
                    start.Set();
                    Check(Task.WaitAll([registering, publishing], TimeSpan.FromSeconds(5)), "Registration race did not finish.");
                    ObserverRegistration registration = registering.GetAwaiter().GetResult();
                    using ObserverSubscription subscription = registration.Subscription;
                    Check(registration.Snapshot.BaseSeq is 0 or 1, "Race returned an impossible reset point.");
                    Publish(fixture.Hub, Status(2, 1));
                    Check(subscription.Reader.TryRead(out object? first), "Race lost the first post-snapshot envelope.");
                    long firstSeq = first is StepBatchMessage step ? step.Seq : ((AgentStatusMessage)first!).Seq;
                    Equal(registration.Snapshot.BaseSeq + 1, firstSeq);
                    var reducer = VisualState.FromSnapshot(registration.Snapshot);
                    reducer.Apply(first!);
                    while (subscription.Reader.TryRead(out object? next)) reducer.Apply(next);
                    Equal(ObserverCodec.Encode(fixture.Hub.CurrentSnapshot()), ObserverCodec.Encode(reducer.Snapshot()));
                }
            }),
            ("Observer completion drains retained messages and prevents further registration or publication", () =>
            {
                var fixture = Create(root);
                using ObserverSubscription subscriber = fixture.Hub.Register(2).Subscription;
                Publish(fixture.Hub, Advance(fixture));
                fixture.Hub.Complete();
                Throws<InvalidOperationException>(() => fixture.Hub.Register());
                Throws<InvalidOperationException>(() => fixture.Hub.Prepare(Status(2, 1)));
                Check(subscriber.Reader.TryRead(out object? first), "Completion discarded retained publication.");
                Equal(1L, ((StepBatchMessage)first!).Seq);
                subscriber.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Check(!subscriber.RequiresResync, "Normal completion was reported as congestion.");
                fixture.Hub.Complete();
            }),
            ("Observer capacity and subscriber limits reject invalid registration and recover on detach", () =>
            {
                var fixture = Create(root);
                Throws<ArgumentOutOfRangeException>(() => fixture.Hub.Register(0));
                Throws<ArgumentOutOfRangeException>(() => fixture.Hub.Register(1025));
                var subscriptions = new List<ObserverSubscription>();
                try
                {
                    for (int i = 0; i < 32; i++) subscriptions.Add(fixture.Hub.Register(i == 0 ? 1024 : 1).Subscription);
                    Throws<InvalidOperationException>(() => fixture.Hub.Register());
                    subscriptions[0].Dispose();
                    subscriptions[0].Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    using ObserverSubscription replacement = fixture.Hub.Register().Subscription;
                }
                finally { foreach (ObserverSubscription subscription in subscriptions) subscription.Dispose(); }
            }),
            ("Observer stale prepared publications fail without changing the current reset point", () =>
            {
                var fixture = Create(root);
                ObserverHub.Prepared first = fixture.Hub.Prepare(Status(1, 0));
                ObserverHub.Prepared stale = fixture.Hub.Prepare(Status(1, 0) with { AgentStatus = AgentStatusDto.Stopped });
                fixture.Hub.Publish(first);
                string before = ObserverCodec.Encode(fixture.Hub.CurrentSnapshot());
                Throws<InvalidOperationException>(() => fixture.Hub.Publish(stale));
                Equal(before, ObserverCodec.Encode(fixture.Hub.CurrentSnapshot()));
                fixture.Hub.Complete();
                Throws<InvalidOperationException>(() => fixture.Hub.Publish(first));
            })
        };
        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M4 Hub: {tests.Length - failed}/{tests.Length} passed.");
        return failed;
    }

    private sealed record Fixture(Game Game, ObserverProjection Projection, ObserverHub Hub);
    private static Fixture Create(string root)
    {
        Game game = Game.Create(ScenarioService.ToCoreScenario(ScenarioService.Read(Path.Combine(root,
            "tests", "Fixtures", "Core", "facility-small.json"))));
        var projection = new ObserverProjection(game.Capture(), game.Observe());
        var hub = new ObserverHub(new() { RunId = "hub-tests", State = projection.State, AgentStatus = AgentStatusDto.Waiting });
        return new(game, projection, hub);
    }
    private static StepBatchMessage Advance(Fixture fixture)
    {
        StepResult step = fixture.Game.Step(GameAction.Move(Direction.East));
        return new()
        {
            RunId = "hub-tests", Seq = fixture.Hub.CurrentSnapshot().BaseSeq + 1, Tick = step.Tick,
            AgentStatus = AgentStatusDto.ActionReceived, Events = ObserverProjection.Events(step),
            Patch = fixture.Projection.Apply(fixture.Game.Observe())
        };
    }
    private static AgentStatusMessage Status(long seq, long tick) => new()
    { RunId = "hub-tests", Seq = seq, Tick = tick, AgentStatus = AgentStatusDto.Waiting };
    private static void Publish(ObserverHub hub, object envelope) => hub.Publish(hub.Prepare(envelope));
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
