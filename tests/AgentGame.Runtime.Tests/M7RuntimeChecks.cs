using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Agents;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

internal static class M7RuntimeChecks
{
    public static async Task<int> RunAsync(string root)
    {
        string directory = Path.Combine(root, "artifacts", "m73-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? baseline = null;
        string? baselineHash = null;
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("M7 human + process run completes and all selected views replay independently", async () =>
            {
                foreach (string view in new[] { "spectator", "agent:0", "agent:1" })
                {
                    string path = Path.Combine(directory, view.Replace(':', '-') + ".jsonl");
                    var result = await MultiGameRunner.RunAsync(Scene(), [HumanRoute(), ProcessSource("wait")],
                        options: new() { RecordPath = path, RecordView = view });
                    Equal("success", result.Kind); Equal(25L, result.Tick); Require(result.Error is null);
                    Equal(true, result.Terminated); Equal(false, result.Truncated);
                    Require(result.Seats.All(s => !s.Retired && s.Cleanup?.Error is null));
                    Dead(result.Seats[1].Cleanup!.ProcessId!.Value);
                    var verification = MultiReplayService.Verify(path);
                    Require(verification.Valid, verification.Error); Equal(result.CoreHash, verification.CoreHash);
                    var envelopes = new List<object>();
                    var summary = MultiReplayService.Export(path, envelopes.Add);
                    Equal("completed", summary.Status); Equal(25L, summary.LastTick); Equal(51L, summary.LastSeq);
                    Equal(53, summary.Lines); Equal(52, envelopes.Count);
                    var reducer = MultiVisualState.FromSnapshot((MultiSnapshotMessage)envelopes[0]);
                    foreach (object envelope in envelopes.Skip(1)) reducer.Apply(envelope);
                    Equal(EpisodeKindDto.Success, reducer.Snapshot().State.Episode!.Kind);
                    if (view != "spectator") Require(reducer.Snapshot().State.Entities.Where(e => e.Kind == "seat")
                        .All(e => e.Id == "seat:" + view[^1]), "Another seat entity leaked into replay.");
                    baseline ??= path; baselineHash ??= result.CoreHash; Equal(baselineHash, result.CoreHash);
                }
            }),
            ("M7 silent process retires once, records wait, and leaves the healthy seat progressing", async () =>
            {
                string path = Path.Combine(directory, "timeout.jsonl");
                var result = await MultiGameRunner.RunAsync(Scene(), [HumanRoute(), ProcessSource("silent")],
                    FastTimeouts(), options: new() { RecordPath = path });
                Equal("success", result.Kind); Equal(25L, result.Tick); Require(result.Error is null);
                Require(result.Seats[1].Retired); Equal("decision_timeout", result.Seats[1].Failure!.Code);
                Dead(result.Seats[1].Cleanup!.ProcessId!.Value);
                using var reader = new MultiReplayReader(path);
                var actions = new List<MultiReplayStepRecord>();
                while (reader.ReadNext() is { } record) if (record is MultiReplayStepRecord step) actions.Add(step);
                Equal(12, actions.Count(s => s.Seat == 1)); Require(actions.Where(s => s.Seat == 1).All(s => s.Action.Type == ActionTypeDto.Wait));
                Require(MultiReplayService.Verify(path).Valid);
            }),
            ("M7 wrong protocol and request id retire the process with diagnostic cleanup", async () =>
            {
                foreach (string mode in new[] { "wrong_ready", "wrong_request" })
                {
                    string path = Path.Combine(directory, mode + ".jsonl");
                    var result = await MultiGameRunner.RunAsync(Scene(), [HumanRoute(), ProcessSource(mode)],
                        options: new() { RecordPath = path });
                    Equal("success", result.Kind); Equal("protocol_violation", result.Seats[1].Failure!.Code);
                    Require(result.Seats[1].Retired); Dead(result.Seats[1].Cleanup!.ProcessId!.Value);
                    Require(MultiReplayService.Verify(path).Valid);
                }
            }),
            ("M7 invalid callback action and late timeout response cannot reach Core", async () =>
            {
                int invalidCalls = 0;
                var invalid = new HumanMultiSeatSource((_, _) => { invalidCalls++; return Task.FromResult(new ActionRequestDto
                    { Type = ActionTypeDto.Pickup, Direction = DirectionDto.East }); });
                var invalidResult = await MultiGameRunner.RunAsync(Scene(), [HumanRoute(), invalid]);
                Equal("success", invalidResult.Kind); Equal(1, invalidCalls); Require(invalidResult.Seats[1].Retired);
                int lateCalls = 0;
                var late = new TaskCompletionSource<ActionRequestDto>(TaskCreationOptions.RunContinuationsAsynchronously);
                var lateSource = new HumanMultiSeatSource((_, _) => { lateCalls++; return late.Task; });
                var result = await MultiGameRunner.RunAsync(Scene(), [HumanRoute(), lateSource], FastTimeouts());
                Equal("success", result.Kind); Equal(1, lateCalls); Equal("decision_timeout", result.Seats[1].Failure!.Code);
                late.SetException(new IOException("late decision fault"));
                Equal(baselineHash, result.CoreHash);
            }),
            ("M7 cancellation closes two process trees without an extra rule step", async () =>
            {
                using var cancellation = new CancellationTokenSource();
                string[] pidFiles = [Path.Combine(directory, "child0.pid"), Path.Combine(directory, "child1.pid")];
                var writer = new CapturingWriter(record => { if (record is MultiReplayStatusRecord) cancellation.Cancel(); });
                var result = await MultiGameRunner.RunAsync(Scene(), [ProcessSource("tree", pidFiles[0]), ProcessSource("tree", pidFiles[1])],
                    cancellationToken: cancellation.Token, options: new() { ReplayWriter = writer });
                Equal("cancelled", result.Kind); Equal(0L, result.Tick); Equal("cancelled", result.Error!.Code);
                Equal(2, result.Seats.Length);
                foreach (var seat in result.Seats) { Require(seat.Cleanup?.Error is null); Dead(seat.Cleanup!.ProcessId!.Value); }
                foreach (string pidFile in pidFiles) Dead(int.Parse(File.ReadAllText(pidFile)));
                Require(writer.Records.All(r => r is not MultiReplayStepRecord));
                Equal("aborted", ((MultiReplayRunFooter)writer.Records[^1]).Status);
            }),
            ("M7 flush failure never publishes the pending step or advances the run summary", async () =>
            {
                var stream = new FlushFailureStream(3);
                var writer = new MultiReplayFileWriter(stream, leaveOpen: true);
                MultiObserverSession? session = null;
                var registrations = new List<MultiObserverRegistration>();
                int calls = 0;
                var source = new HumanMultiSeatSource((_, _) => { calls++; return Task.FromResult(Route()[0]); });
                var result = await MultiGameRunner.RunAsync(Scene(), [source, new WaitMultiSeatSource()],
                    options: new() { ReplayWriter = writer, ObserverReady = s => { session = s;
                        registrations.AddRange(s.Views.Select(v => s.GetHub(v).Register())); } });
                Equal("execution_error", result.Kind); Equal("record_error", result.Error!.Code); Equal(0L, result.Tick); Equal(1, calls);
                Equal(MultiStateEncoding.Hash(MultiGame.Create(MultiScenarioService.ToCoreScenario(Scene())).Capture()), result.CoreHash);
                foreach (var registration in registrations)
                {
                    Equal(0L, session!.GetHub(registration.Snapshot.View).CurrentSnapshot().Tick);
                    while (registration.Subscription.Reader.TryRead(out var message)) Require(message is not MultiStepBatchMessage);
                    registration.Subscription.Dispose();
                }
                Equal(3, stream.Flushes);
            }),
            ("M7 header failure closes unstarted sources and does not expose an observer", async () =>
            {
                var first = new ProbeSource(); var second = new ProbeSource(); bool ready = false;
                var result = await MultiGameRunner.RunAsync(Scene(), [first, second], options: new()
                { ReplayWriter = new MultiReplayFileWriter(new FlushFailureStream(1)), ObserverReady = _ => ready = true });
                Equal("record_error", result.Error!.Code); Equal(0L, result.Tick); Require(!ready);
                Equal(0, first.Starts + second.Starts); Equal(1, first.Closes); Equal(1, second.Closes);
            }),
            ("M7 cancellation before startup releases injected writer and every unstarted source", async () =>
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                var writer = new CapturingWriter(); var first = new ProbeSource(); var second = new ProbeSource(); bool ready = false;
                var result = await MultiGameRunner.RunAsync(Scene(), [first, second], cancellationToken: cancellation.Token,
                    options: new() { ReplayWriter = writer, ObserverReady = _ => ready = true });
                Equal("cancelled", result.Kind); Equal(0L, result.Tick); Equal(0, writer.Records.Count); Equal(1, writer.Disposes);
                Equal(0, first.Starts + second.Starts); Equal(1, first.Closes); Equal(1, second.Closes); Require(!ready);
            }),
            ("M7 cleanup failure remains an execution error with the committed rule result", async () =>
            {
                var result = await MultiGameRunner.RunAsync(Scene(), [HumanRoute(), new ProbeSource(cleanupFailure: true)]);
                Equal("execution_error", result.Kind); Equal("cleanup_failed", result.Error!.Code);
                Equal(25L, result.Tick); Equal(baselineHash, result.CoreHash); Require(result.Terminated);
            }),
            ("M7 replay detects wrong seat, hash, events, patch and initial state", () =>
            {
                AssertTamper("seat", n => FirstStep(n)["seat"] = 1, "seat_turn_mismatch");
                AssertTamper("hash", n => FirstStep(n)["core_hash"] = new string('0', 64), "core_hash_mismatch");
                AssertTamper("events", n => FirstStep(n)["observer_batch"]!["events"] = new JsonArray(), "events_mismatch");
                AssertTamper("patch", n => FirstStep(n)["observer_batch"]!["patch"]!["entity_upserts"]![0]!["x"] = 3, "observer_patch_mismatch");
                AssertTamper("initial", n => n[0]["initial_snapshot"]!["state"]!["tiles"]![0]!["terrain"] = "floor", "initial_snapshot_mismatch");
                return Task.CompletedTask;
            }),
            ("M7 incomplete tails and missing footer retain only complete JSONL records", () =>
            {
                string[] lines = File.ReadAllLines(baseline!);
                string missing = Path.Combine(directory, "missing.jsonl"); File.WriteAllLines(missing, lines[..^1], Utf8);
                Equal("incomplete", MultiReplayService.Export(missing, _ => { }).Status);
                Equal("incomplete", MultiReplayService.Verify(missing).Status);
                string partial = Path.Combine(directory, "partial.jsonl");
                File.WriteAllText(partial, string.Join('\n', lines[..^3]) + "\n" + lines[^3][..20], Utf8);
                var summary = MultiReplayService.Export(partial, _ => { }); Equal("incomplete", summary.Status); Equal(24L, summary.LastTick);
                Require(!MultiReplayService.Verify(partial).Valid);
                return Task.CompletedTask;
            }),
            ("M7 replay rejects gaps, foreign identity, absent view and data after footer", () =>
            {
                AssertTamper("gap", n => n[1]["observer_status"]!["seq"] = 2, "sequence_gap");
                AssertTamper("identity", n => FirstStep(n)["observer_batch"]!["run_id"] = "foreign", "observer_identity_mismatch");
                AssertTamper("view", n => { var snapshot = n[0]["initial_snapshot"]!; snapshot["view"] = "agent:2";
                    var entities = snapshot["state"]!["entities"]!.AsArray();
                    foreach (var entity in entities.ToArray()) if (entity!["kind"]!.GetValue<string>() == "seat")
                    { if (entity["id"]!.GetValue<string>() == "seat:0") entity["id"] = "seat:2"; else entities.Remove(entity); }
                }, "view_seat_out_of_range");
                string path = Path.Combine(directory, "extra.jsonl"); File.WriteAllText(path, File.ReadAllText(baseline!) + "x", Utf8);
                Equal("data_after_footer", MultiReplayService.Verify(path).Error);
                return Task.CompletedTask;
            }),
            ("M7 reader clones headers and latches the first ordering error", () =>
            {
                using (var reader = new MultiReplayReader(baseline!))
                {
                    string expected = MultiReplayCodec.Encode(reader.Header);
                    reader.Header.Scenario.Rows[0] = "changed"; reader.Header.InitialSnapshot.State.Entities[0] = new();
                    Equal(expected, MultiReplayCodec.Encode(reader.Header));
                }
                var nodes = Nodes(); nodes[1]["observer_status"]!["seq"] = 2; string path = Write("sticky", nodes);
                using var bad = new MultiReplayReader(path);
                var first = Throws<ReplayFormatException>(() => bad.ReadNext());
                Require(ReferenceEquals(first, Throws<ReplayFormatException>(() => bad.ReadNext()))); Equal(0L, bad.LastSeq);
                return Task.CompletedTask;
            }),
            ("M7 future rules remain playable and are refused by verification", () =>
            {
                var nodes = Nodes(); nodes[0]["rules"] = "future/99"; nodes[0]["scenario"]!["rules"] = "future/99";
                string path = Write("future", nodes); Equal("completed", MultiReplayService.Export(path, _ => { }).Status);
                Equal("unsupported_version", MultiReplayService.Verify(path).Status); return Task.CompletedTask;
            }),
            ("M7 writer refuses overwrite and latches flush failure; scene files roundtrip", async () =>
            {
                string existing = Path.Combine(directory, "existing.jsonl"); File.WriteAllText(existing, "preserve", Utf8);
                Throws<IOException>(() => new MultiReplayFileWriter(existing)); Equal("preserve", File.ReadAllText(existing));
                var stream = new FlushFailureStream(1);
                await using var writer = new MultiReplayFileWriter(stream, leaveOpen: true);
                object header = MultiReplayCodec.Parse(File.ReadAllLines(baseline!)[0]);
                await ThrowsAsync<IOException>(() => writer.AppendAsync(header).AsTask()); long length = stream.Length;
                await ThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(header).AsTask()); Equal(length, stream.Length);
                string scenePath = Path.Combine(directory, "scene.json"); MultiScenarioService.Write(scenePath, Scene());
                Equal(MultiScenarioCodec.Encode(Scene()), MultiScenarioCodec.Encode(MultiScenarioService.Read(scenePath)));
                Require(MultiScenarioService.Validate(Scene()).Valid);
                Throws<IOException>(() => MultiScenarioService.Write(scenePath, Scene()));
            }),
            ("M7 invalid UTF8, oversized lines and duplicate sources fail before execution", async () =>
            {
                Throws<ReplayFormatException>(() => new MultiReplayReader(new MemoryStream([0xc3, 0x28, 10])));
                var oversized = new MemoryStream(new byte[MultiReplayCodec.MaxRecordBytes + 2]);
                Equal("line_too_long", Throws<ReplayFormatException>(() => new MultiReplayReader(oversized)).Code);
                var source = new ProbeSource(); string path = Path.Combine(directory, "invalid.jsonl");
                await ThrowsAsync<ArgumentException>(() => MultiGameRunner.RunAsync(Scene(), [source, source], options: new() { RecordPath = path }));
                Equal(0, source.Starts); Require(!File.Exists(path));
            })
        };
        int failed = 0;
        foreach (var (name, check) in checks)
            try { await check(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        Console.WriteLine($"M7 Runtime: {checks.Length - failed}/{checks.Length} passed.");
        return failed;

        ProcessMultiSeatSource ProcessSource(string mode, string? pidFile = null)
        {
            var arguments = new List<string> { "-u", Path.Combine(root, "tests", "Fixtures", "Agents", "v2_fixture_agent.py"), "--mode", mode };
            if (pidFile is not null) arguments.AddRange(["--pid-file", pidFile]);
            return new(new AgentCommand(Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python", arguments));
        }
        JsonObject[] Nodes() => File.ReadAllLines(baseline!).Select(s => JsonNode.Parse(s)!.AsObject()).ToArray();
        string Write(string name, JsonObject[] nodes) { string path = Path.Combine(directory, name + ".jsonl");
            File.WriteAllLines(path, nodes.Select(n => n.ToJsonString()), Utf8); return path; }
        void AssertTamper(string name, Action<JsonObject[]> mutate, string error)
        { var nodes = Nodes(); mutate(nodes); Equal(error, MultiReplayService.Verify(Write(name, nodes)).Error); }
    }

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static JsonObject FirstStep(JsonObject[] nodes) => nodes.First(n => n["type"]!.GetValue<string>() == "step_record");
    private static MultiScenarioDto Scene() => new() { Rows = ["#########", "#.......#", "#########"],
        Spawns = [new() { X = 1, Y = 1 }, new() { X = 3, Y = 1 }], Exit = new() { X = 1, Y = 1 },
        Key = new() { X = 2, Y = 1 }, Door = new() { X = 4, Y = 1 }, Core = new() { X = 6, Y = 1 }, MaxTicks = 64, VisibilityRadius = 1 };
    private static ActionRequestDto[] Route() => [Move(DirectionDto.East), new() { Type = ActionTypeDto.Pickup }, Move(DirectionDto.East),
        new() { Type = ActionTypeDto.Interact, Direction = DirectionDto.East }, Move(DirectionDto.East), Move(DirectionDto.East), Move(DirectionDto.East),
        new() { Type = ActionTypeDto.Pickup }, Move(DirectionDto.West), Move(DirectionDto.West), Move(DirectionDto.West), Move(DirectionDto.West), Move(DirectionDto.West)];
    private static ActionRequestDto Move(DirectionDto direction) => new() { Type = ActionTypeDto.Move, Direction = direction };
    private static HumanMultiSeatSource HumanRoute() { var actions = new Queue<ActionRequestDto>(Route());
        return new((observation, _) => { Equal(0, observation.Seat); Require(observation.Observation.Inventory.Length <= 2);
            if (observation.Tick == 0) Require(observation.Observation.Tiles.All(t => t.X <= 2), "Another seat's tiles leaked.");
            return Task.FromResult(actions.Dequeue()); }); }
    private static AgentTimeouts FastTimeouts() => new() { Decision = TimeSpan.FromMilliseconds(300) };
    private static void Dead(int pid) { try { using var process = Process.GetProcessById(pid); Require(process.HasExited, "Process still alive: " + pid); }
        catch (ArgumentException) { } }
    private static void Require(bool value, string? detail = null) { if (!value) throw new Exception(detail ?? "Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class CapturingWriter(Action<object>? appended = null) : IReplayWriter
    {
        internal readonly List<object> Records = [];
        internal int Disposes;
        public ValueTask AppendAsync(object record, CancellationToken cancellationToken = default)
        { Records.Add(MultiReplayCodec.Parse(MultiReplayCodec.Encode(record))); appended?.Invoke(record); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }
    private sealed class FlushFailureStream(int failAt) : MemoryStream
    {
        internal int Flushes;
        public override Task FlushAsync(CancellationToken cancellationToken) => ++Flushes == failAt
            ? Task.FromException(new IOException("injected flush failure")) : Task.CompletedTask;
    }
    private sealed class ProbeSource(bool cleanupFailure = false) : IMultiSeatSource
    {
        internal int Starts, Closes;
        public Task<string> StartAsync(int seat, int seatCount, CancellationToken token) { Starts++; return Task.FromResult("probe"); }
        public Task<ActionRequestDto> DecideAsync(AgentV2ObservationMessage message, CancellationToken token) => Task.FromResult(new ActionRequestDto { Type = ActionTypeDto.Wait });
        public Task EndAsync(AgentV2EpisodeEndMessage message, CancellationToken token) => Task.CompletedTask;
        public Task<MultiSeatCleanup> CloseAsync(bool graceful) { Closes++; return Task.FromResult(new MultiSeatCleanup(null, null, false,
            cleanupFailure ? new("cleanup_failed", "shutdown", "injected cleanup failure") : null, "")); }
    }
}
