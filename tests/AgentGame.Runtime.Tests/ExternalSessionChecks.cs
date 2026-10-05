using System.Text;
using System.Text.Json;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

internal static class ExternalSessionChecks
{
    public static async Task<int> RunAsync(string root)
    {
        string directory = Path.Combine(root, "artifacts", "external-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ScenarioDto Scene() => ScenarioService.Read(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json"));
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("External idle reads keep tick zero and expose only local observation", async () =>
            {
                ScenarioDto scene = Scene();
                await using var session = await ExternalGameSession.CreateAsync(scene);
                scene.Rows[1] = "#########";
                string first = await session.ObserveJsonAsync();
                await Task.Delay(40);
                Equal(first, await session.ObserveJsonAsync());
                var status = await session.GetStatusAsync(); Equal(0L, status.Tick); Equal("waiting", status.State);
                Require(status.RequestId!.StartsWith(session.SessionId + ":r", StringComparison.Ordinal));
                using var json = JsonDocument.Parse(first);
                var observation = json.RootElement.GetProperty("observation");
                Require(!json.RootElement.TryGetProperty("rows", out _) && !json.RootElement.TryGetProperty("seed", out _));
                Require(observation.GetProperty("tiles").EnumerateArray().All(t => t.GetProperty("x").GetInt32() <= 4));
                Equal("observation", json.RootElement.GetProperty("type").GetString());
            }),
            ("External invalid, stale and foreign-session actions leave ticks unchanged", async () =>
            {
                await using var session = await ExternalGameSession.CreateAsync(Scene(), new() { RunId = "reused" });
                await using var other = await ExternalGameSession.CreateAsync(Scene(), new() { RunId = "reused" });
                string id = (await session.GetStatusAsync()).RequestId!;
                foreach (byte[] bad in new[] {
                    Encoding.UTF8.GetBytes("not json"), new byte[] { 0xc3, 0x28 },
                    Encoding.UTF8.GetBytes("{\"type\":\"action\",\"type\":\"action\"}"),
                    Encoding.UTF8.GetBytes("{\"type\":\"ready\",\"protocol\":\"agent/1\",\"name\":\"x\"}"),
                    Action(id, "move"), Action(id, "pickup", "east"), Action(id, "wait", extra: true) })
                    Equal("invalid_action", (await Error(() => session.SubmitJsonAsync(bad))).Code);
                Equal("stale_request", (await Error(() => session.SubmitJsonAsync(Action("old", "wait")))).Code);
                string foreignId = (await other.GetStatusAsync()).RequestId!;
                Equal("stale_request", (await Error(() => session.SubmitJsonAsync(Action(foreignId, "wait")))).Code);
                Equal(0L, (await session.GetStatusAsync()).Tick);
                await session.SubmitJsonAsync(Action(id, "wait"));
                Equal("stale_request", (await Error(() => session.SubmitJsonAsync(Action(id, "wait")))).Code);
                Equal(1L, (await session.GetStatusAsync()).Tick);
            }),
            ("External competing controllers accept a request exactly once", async () =>
            {
                await using var session = await ExternalGameSession.CreateAsync(Scene());
                string id = (await session.GetStatusAsync()).RequestId!;
                async Task<bool> Submit()
                {
                    try { await session.SubmitJsonAsync(Action(id, "wait")); return true; }
                    catch (ExternalSessionException error) { Equal("stale_request", error.Code); return false; }
                }
                bool[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(Submit)));
                Equal(1, results.Count(r => r)); Equal(1L, (await session.GetStatusAsync()).Tick);
            }),
            ("External successful run completes replay once and keeps terminal queries available", async () =>
            {
                string record = Path.Combine(directory, "success.jsonl");
                var session = await ExternalGameSession.CreateAsync(Scene(), new() { RecordPath = record });
                try
                {
                    using var route = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.actions.json")));
                    string last = "";
                    foreach (var action in route.RootElement.EnumerateArray())
                    {
                        string id = (await session.GetStatusAsync()).RequestId!;
                        byte[] bytes = Encoding.UTF8.GetBytes("{\"type\":\"action\",\"request_id\":\"" + id + "\",\"action\":" + action.GetRawText() + "}");
                        last = await session.SubmitJsonAsync(bytes);
                    }
                    Equal(13L, (await session.GetStatusAsync()).Tick); Equal("completed", (await session.GetStatusAsync()).State);
                    Equal(last, await session.ObserveJsonAsync());
                    Require(last.Contains("\"type\":\"episode_end\"", StringComparison.Ordinal));
                    Equal("episode_ended", (await Error(() => session.SubmitJsonAsync(Action("anything", "wait")))).Code);
                    var verified = ReplayService.Verify(record); Require(verified.Valid, verified.Error);
                    Equal("a68fe87640e7105142f481a59e77f43bdb854bab61a826e38ae4fd77ae323262", verified.CoreHash);
                    Equal(1, File.ReadLines(record).Count(l => l.Contains("\"type\":\"run_footer\"", StringComparison.Ordinal)));
                }
                finally { await session.DisposeAsync(); await session.DisposeAsync(); }
                Require(ReplayService.Verify(record).Valid);
            }),
            ("External disposal aborts an unfinished replay and closes the writer once", async () =>
            {
                var writer = new Writer();
                var session = await ExternalGameSession.CreateAsync(Scene(), new() { ReplayWriter = writer });
                await session.DisposeAsync(); await session.DisposeAsync();
                Equal(1, writer.Disposes); Equal("closed", (await session.GetStatusAsync()).State);
                Equal("aborted", writer.Records.OfType<ReplayRunFooter>().Single().Status);
                Equal("session_closed", (await Error(() => session.ObserveJsonAsync())).Code);
                Equal("session_closed", (await Error(() => session.SubmitJsonAsync(Action("any", "wait")))).Code);
                Require(!writer.Records.OfType<ReplayStepRecord>().Any());
            }),
            ("External failed authority step never publishes or exposes the advanced game", async () =>
            {
                var writer = new Writer { FailSteps = true };
                ObserverRegistration? registration = null;
                await using var session = await ExternalGameSession.CreateAsync(Scene(), new()
                { ReplayWriter = writer, ObserverReady = hub => registration = hub.Register() });
                string id = (await session.GetStatusAsync()).RequestId!;
                Equal("record_error", (await Error(() => session.SubmitJsonAsync(Action(id, "move", "east")))).Code);
                var status = await session.GetStatusAsync(); Equal(0L, status.Tick); Equal("failed", status.State);
                Equal("record_error", status.Error); Require(status.RequestId is null);
                Require(!registration!.Subscription.Reader.TryRead(out _));
                Equal("session_failed", (await Error(() => session.ObserveJsonAsync())).Code);
                Equal("session_failed", (await Error(() => session.SubmitJsonAsync(Action(id, "wait")))).Code);
                registration.Subscription.Dispose();
            }),
            ("External startup failure and precancellation release the owned writer", async () =>
            {
                var fail = new Writer { FailHeader = true };
                bool ready = false;
                Equal("record_error", (await Error(async () => { await ExternalGameSession.CreateAsync(Scene(), new()
                { ReplayWriter = fail, ObserverReady = _ => ready = true }); })).Code);
                Require(!ready); Equal(1, fail.Disposes);
                var cancelWriter = new Writer(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await ExternalGameSession.CreateAsync(Scene(), new() { ReplayWriter = cancelWriter }, cancelled.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { }
                Equal(0, cancelWriter.Records.Count); Equal(1, cancelWriter.Disposes);
                var callbackWriter = new Writer();
                try { await ExternalGameSession.CreateAsync(Scene(), new()
                    { ReplayWriter = callbackWriter, ObserverReady = _ => throw new InvalidOperationException("callback failed") }); throw new Exception("Expected callback failure"); }
                catch (InvalidOperationException) { }
                Equal(1, callbackWriter.Disposes);
            }),
            ("External footer and disposal errors surface without repeated disposal", async () =>
            {
                foreach (bool footerFailure in new[] { true, false })
                {
                    var writer = new Writer { FailFooter = footerFailure, FailDispose = !footerFailure };
                    var session = await ExternalGameSession.CreateAsync(Scene(), new() { ReplayWriter = writer });
                    Equal("record_error", (await Error(async () => await session.DisposeAsync())).Code);
                    Equal(1, writer.Disposes); await session.DisposeAsync(); Equal(1, writer.Disposes);
                }
            }),
            ("External terminal recording errors retain the committed rule result separately from execution failure", async () =>
            {
                foreach (bool footerFailure in new[] { true, false })
                {
                    var writer = new Writer { FailFooter = footerFailure, FailDispose = !footerFailure };
                    var session = await ExternalGameSession.CreateAsync(Scene(), new() { ReplayWriter = writer });
                    try
                    {
                        using var route = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.actions.json")));
                        int index = 0;
                        foreach (var action in route.RootElement.EnumerateArray())
                        {
                            string id = (await session.GetStatusAsync()).RequestId!;
                            byte[] bytes = Encoding.UTF8.GetBytes("{\"type\":\"action\",\"request_id\":\"" + id + "\",\"action\":" + action.GetRawText() + "}");
                            if (++index < 13) await session.SubmitJsonAsync(bytes);
                            else Equal("record_error", (await Error(() => session.SubmitJsonAsync(bytes))).Code);
                        }
                        var status = await session.GetStatusAsync();
                        Equal("failed", status.State); Equal("record_error", status.Error); Equal(13L, status.Tick);
                        Equal(EpisodeKindDto.Success, status.Result!.Kind); Require(status.RequestId is null);
                        Equal("session_failed", (await Error(() => session.ObserveJsonAsync())).Code);
                        Equal("session_failed", (await Error(() => session.SubmitJsonAsync(Action("any", "wait")))).Code);
                        Equal(footerFailure ? 0 : 1, writer.Records.OfType<ReplayRunFooter>().Count());
                    }
                    finally { await session.DisposeAsync(); await session.DisposeAsync(); }
                    Equal(1, writer.Disposes);
                }
            }),
            ("External cancellation cannot interrupt accepted commits or mutate queued action buffers", async () =>
            {
                var writer = new Writer { HoldStep = true };
                await using var session = await ExternalGameSession.CreateAsync(Scene(), new() { ReplayWriter = writer });
                string id = (await session.GetStatusAsync()).RequestId!;
                using var cancellation = new CancellationTokenSource();
                Task<string> accepted = session.SubmitJsonAsync(Action(id, "wait"), cancellation.Token);
                await writer.StepEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
                byte[] queuedBytes = Action(id, "wait"); Task<string> queued = session.SubmitJsonAsync(queuedBytes);
                Array.Fill(queuedBytes, (byte)'!');
                cancellation.Cancel(); writer.ReleaseStep.TrySetResult();
                await accepted; Equal("stale_request", (await Error(() => queued)).Code);
                Equal(1L, (await session.GetStatusAsync()).Tick);
                try { await session.SubmitJsonAsync(Action(id, "wait"), cancellation.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { }
                Equal(1L, (await session.GetStatusAsync()).Tick);
            })
        };
        int failed = 0;
        foreach (var (name, run) in checks)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Console.WriteLine($"External sessions: {checks.Length - failed}/{checks.Length} passed.");
        return failed;
    }

    private static byte[] Action(string id, string type, string? direction = null, bool extra = false) =>
        Encoding.UTF8.GetBytes("{\"type\":\"action\",\"request_id\":\"" + id + "\",\"action\":{\"type\":\"" + type + "\"" +
            (direction is null ? "" : ",\"direction\":\"" + direction + "\"") + (extra ? ",\"extra\":true" : "") + "}}");
    private static async Task<ExternalSessionException> Error(Func<Task> run)
    { try { await run(); } catch (ExternalSessionException error) { return error; } throw new Exception("Expected ExternalSessionException"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Require(bool condition, string? detail = null) { if (!condition) throw new Exception(detail ?? "Assertion failed"); }
    private sealed class Writer : IReplayWriter
    {
        public readonly List<object> Records = [];
        public bool FailHeader, FailSteps, FailFooter, FailDispose, HoldStep;
        public int Disposes;
        public TaskCompletionSource StepEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStep = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask AppendAsync(object record, CancellationToken cancellationToken = default)
        {
            if ((FailHeader && record is ReplayRunHeader) || (FailSteps && record is ReplayStepRecord) || (FailFooter && record is ReplayRunFooter))
                throw new IOException("Injected authority failure");
            if (HoldStep && record is ReplayStepRecord)
            { StepEntered.TrySetResult(); await ReleaseStep.Task.WaitAsync(cancellationToken); }
            Records.Add(record);
        }
        public ValueTask DisposeAsync()
        { Disposes++; if (FailDispose) throw new IOException("Injected dispose failure"); return ValueTask.CompletedTask; }
    }
}
