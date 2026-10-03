using System.Diagnostics;
using System.Text;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Agents;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

internal static class M4IntegrationChecks
{
    private const string InitialHash = "932e1d591f8e19764177838757f3117c8dbcd19e202e47f806ff4bb50c49d286";
    private const string FirstMoveHash = "fb52d983a5762378c8987ad0903ab7e9c879eadc2fdbb0eee9a76ae3bbafa744";
    private const string SuccessHash = "a68fe87640e7105142f481a59e77f43bdb854bab61a826e38ae4fd77ae323262";

    public static async Task<int> RunAsync(string root)
    {
        ScenarioDto scene = ScenarioService.Read(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json"));
        string fixture = Path.Combine(root, "tests", "Fixtures", "Agents", "fault_agent.py");
        string python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        string directory = Path.Combine(root, "artifacts", "m4-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var limits = new AgentTimeouts
        {
            Handshake = TimeSpan.FromSeconds(2), Decision = TimeSpan.FromSeconds(2),
            ShutdownGrace = TimeSpan.FromMilliseconds(150), KillWait = TimeSpan.FromSeconds(1)
        };
        AgentCommand Command(string mode) => new(python, ["-u", fixture, "--mode", mode], root);
        async Task<RunResult> Run(string mode, RunOptions options, ScenarioDto? scenario = null, AgentTimeouts? timeouts = null)
        {
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await GameRunner.RunAsync(scenario ?? scene, Command(mode), timeouts ?? limits, watchdog.Token, options)
                .WaitAsync(TimeSpan.FromSeconds(12));
        }
        string PathFor(string name) => Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        string Save(CaptureWriter writer)
        {
            string path = PathFor("captured-prefix");
            File.WriteAllText(path, string.Join("\n", writer.Records.Select(ReplayCodec.Encode)) + "\n", new UTF8Encoding(false, true));
            return path;
        }
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Recorded external success verifies every committed step and matches the live Observer", async () =>
            {
                string path = PathFor("success"); ObserverHub? hub = null; ObserverRegistration? registration = null;
                RunResult result = await Run("success", new()
                {
                    RecordPath = path, RunId = "integration-success",
                    ObserverReady = owner => { hub = owner; registration = owner.Register(128); }
                });
                Gone(result); Equal("success", result.Kind); Equal(13L, result.Tick); Equal(SuccessHash, result.CoreHash);
                Require(result.Error is null, "Success recording reported failure.");
                VerificationResult verified = ReplayService.Verify(path); Require(verified.Valid, verified.Error ?? "Replay failed verification.");
                Equal("completed", verified.Status); Equal(result.CoreHash, verified.CoreHash);
                using (var reader = new ReplayReader(path))
                {
                    Equal("fault-agent", reader.Header.AgentName); int steps = 0;
                    while (reader.ReadNext() is { } record) if (record is ReplayStepRecord) steps++;
                    Equal(13, steps); Equal("completed", reader.Status);
                }
                Require(hub is not null && registration is not null, "Observer was not initialized.");
                var live = VisualState.FromSnapshot(registration!.Snapshot);
                while (registration!.Subscription.Reader.TryRead(out object? message)) live.Apply(message);
                await registration!.Subscription.Reader.Completion;
                SnapshotMessage current = hub!.CurrentSnapshot();
                Equal(ObserverCodec.Encode(current), ObserverCodec.Encode(live.Snapshot()));
                VisualState? replayed = null;
                ReplaySummary summary = ReplayService.Export(path, message =>
                {
                    if (message is SnapshotMessage snapshot) replayed = VisualState.FromSnapshot(snapshot);
                    else replayed!.Apply(message);
                });
                Equal("completed", summary.Status); Equal(ObserverCodec.Encode(current), ObserverCodec.Encode(replayed!.Snapshot()));
                Equal(13L, current.Tick); Equal(27L, current.BaseSeq); Equal(AgentStatusDto.Stopped, current.AgentStatus);
                registration!.Subscription.Dispose();
            }),
            ("Recorded turn limit is a completed rule episode", async () =>
            {
                string path = PathFor("turn-limit");
                RunResult result = await Run("wait", new() { RecordPath = path }, scene with { MaxTicks = 13 });
                Gone(result); Equal("turn_limit", result.Kind); Equal(13L, result.Tick);
                Require(result.Truncated && !result.Terminated && result.Error is null, "Wrong rule terminal outcome.");
                var verified = ReplayService.Verify(path); Require(verified.Valid, verified.Error ?? "Bad turn limit replay.");
                Equal("completed", verified.Status); Equal(result.CoreHash, verified.CoreHash);
                using var reader = new ReplayReader(path); ReplayRunFooter? footer = null;
                while (reader.ReadNext() is { } record) if (record is ReplayRunFooter found) footer = found;
                Equal(EpisodeKindDto.TurnLimit, footer!.Result!.Kind); Equal(13L, footer.LastTick);
            }),
            ("Decision failure records only accepted turns and finishes aborted", async () =>
            {
                string path = PathFor("decision-failure");
                RunResult result = await Run("valid-then-wrong", new() { RecordPath = path });
                Gone(result); Equal("execution_error", result.Kind); Equal("protocol_violation", result.Error!.Code);
                Equal(1L, result.Tick); Equal("r0", result.LastRequestId); Equal(ActionTypeDto.Wait, result.LastAction!.Type);
                var verified = ReplayService.Verify(path); Require(verified.Valid, verified.Error ?? "Bad aborted prefix.");
                Equal("aborted", verified.Status); Equal(1L, verified.Tick); Equal(result.CoreHash, verified.CoreHash);
                using var reader = new ReplayReader(path); int steps = 0; ReplayRunFooter? footer = null;
                while (reader.ReadNext() is { } record)
                { if (record is ReplayStepRecord) steps++; if (record is ReplayRunFooter found) footer = found; }
                Equal(1, steps); Equal("aborted", footer!.Status); Require(footer.Result is null, "Execution failure invented rule termination.");
            }),
            ("Handshake timeout records an aborted tick zero and leaves no process", async () =>
            {
                string path = PathFor("handshake-timeout");
                RunResult result = await Run("no-read", new() { RecordPath = path }, timeouts: limits with { Handshake = TimeSpan.FromMilliseconds(250) });
                Gone(result); Equal("execution_error", result.Kind); Equal("handshake_timeout", result.Error!.Code);
                Equal(0L, result.Tick); Equal(InitialHash, result.CoreHash); Require(result.LastAction is null, "Handshake failure committed action.");
                var verified = ReplayService.Verify(path); Require(verified.Valid, verified.Error ?? "Bad tick zero replay."); Equal("aborted", verified.Status);
                using var reader = new ReplayReader(path); Equal("", reader.Header.AgentName);
                int steps = 0; while (reader.ReadNext() is { } record) if (record is ReplayStepRecord) steps++;
                Equal(0, steps);
            }),
            ("Failure recording the first step preserves initial authority and publishes no step", async () =>
            {
                var writer = new CaptureWriter(3); ObserverHub? hub = null; ObserverRegistration? registration = null;
                RunResult result = await Run("success", new()
                {
                    ReplayWriter = writer,
                    ObserverReady = owner => { hub = owner; registration = owner.Register(128); }
                });
                Gone(result); Equal("execution_error", result.Kind); Equal("record_error", result.Error!.Code);
                Equal(0L, result.Tick); Equal(InitialHash, result.CoreHash);
                Require(result.LastAction is null && result.LastRequestId is null, "Unrecorded action escaped into summary.");
                Require(hub is not null && registration is not null, "Initial authority was not ready.");
                Equal(0L, hub!.CurrentSnapshot().Tick); Equal(1L, hub!.CurrentSnapshot().BaseSeq);
                int steps = 0, statuses = 0;
                while (registration!.Subscription.Reader.TryRead(out var message))
                { if (message is StepBatchMessage) steps++; if (message is AgentStatusMessage) statuses++; }
                await registration!.Subscription.Reader.Completion;
                Equal(0, steps); Equal(1, statuses); Equal(3, writer.Attempts); Equal(2, writer.Records.Count); Equal(1, writer.DisposeCount);
                Require(writer.Records.All(record => record is not ReplayRunFooter), "Broken writer received a footer.");
                var prefix = ReplayService.Verify(Save(writer)); Equal("incomplete", prefix.Status); Equal(0L, prefix.Tick); Equal(InitialHash, prefix.CoreHash);
                registration!.Subscription.Dispose();
            }),
            ("Failure recording the second step preserves only the first committed action", async () =>
            {
                var writer = new CaptureWriter(5); ObserverHub? hub = null; ObserverRegistration? registration = null;
                RunResult result = await Run("success", new()
                {
                    ReplayWriter = writer,
                    ObserverReady = owner => { hub = owner; registration = owner.Register(128); }
                });
                Gone(result); Equal("record_error", result.Error!.Code); Equal(1L, result.Tick); Equal(FirstMoveHash, result.CoreHash);
                Equal("r0", result.LastRequestId); Equal(ActionTypeDto.Move, result.LastAction!.Type); Equal(DirectionDto.East, result.LastAction.Direction);
                Equal(1L, hub!.CurrentSnapshot().Tick); Equal(3L, hub!.CurrentSnapshot().BaseSeq);
                int steps = 0; while (registration!.Subscription.Reader.TryRead(out var message)) if (message is StepBatchMessage) steps++;
                await registration!.Subscription.Reader.Completion;
                Equal(1, steps); Equal(5, writer.Attempts); Equal(4, writer.Records.Count); Equal(1, writer.DisposeCount);
                Require(writer.Records.All(record => record is not ReplayRunFooter), "Writer continued after failure.");
                var prefix = ReplayService.Verify(Save(writer)); Equal("incomplete", prefix.Status); Equal(1L, prefix.Tick); Equal(FirstMoveHash, prefix.CoreHash);
                registration!.Subscription.Dispose();
            }),
            ("Initial header failure closes the Agent before any Observer registration", async () =>
            {
                var writer = new CaptureWriter(1); int registrations = 0;
                RunResult result = await Run("success", new() { ReplayWriter = writer, ObserverReady = _ => registrations++ });
                Gone(result); Equal("record_error", result.Error!.Code); Equal(0L, result.Tick); Equal(InitialHash, result.CoreHash);
                Equal(0, registrations); Equal(1, writer.Attempts); Equal(0, writer.Records.Count); Equal(1, writer.DisposeCount);
                Require(result.LastAction is null && result.LastRequestId is null, "Header failure accepted action."); Equal("fault-agent", result.AgentName);
            }),
            ("Recording failure on the winning step never tells the Agent the episode ended", async () =>
            {
                // Attempt 27 is the 13th step record: one header, then a waiting status and a
                // step per turn, so step k is attempt 2k + 1. That step is the one that returns
                // the core to the exit and would end the episode.
                var writer = new CaptureWriter(27); ObserverHub? hub = null; ObserverRegistration? registration = null;
                RunResult result = await Run("success-trace", new()
                {
                    ReplayWriter = writer,
                    ObserverReady = owner => { hub = owner; registration = owner.Register(128); }
                });
                Gone(result);
                Equal("execution_error", result.Kind); Equal("record_error", result.Error!.Code);
                Equal(12L, result.Tick);
                Require(!result.Terminated && !result.Truncated, "An unrecorded terminal state reached the summary.");
                Require(result.CoreHash != SuccessHash, "Summary exposed the unrecorded winning state.");
                Require(!result.StderrTail.Contains("episode_end", StringComparison.Ordinal),
                    "The Agent was told the episode ended although the winning step was never recorded: " + result.StderrTail.Trim());
                Require(writer.Records.All(record => record is not ReplayRunFooter), "Broken writer received a footer.");
                Equal(26, writer.Records.Count); Equal(27, writer.Attempts); Equal(1, writer.DisposeCount);
                // Only the last published waiting status follows the 12 committed turns.
                Equal(12L, hub!.CurrentSnapshot().Tick); Equal(25L, hub!.CurrentSnapshot().BaseSeq);
                int published = 0;
                while (registration!.Subscription.Reader.TryRead(out var message)) if (message is StepBatchMessage) published++;
                await registration!.Subscription.Reader.Completion; Equal(12, published);
                string path = Save(writer);
                var prefix = ReplayService.Verify(path);
                Equal("incomplete", prefix.Status); Equal(12L, prefix.Tick); Equal(result.CoreHash, prefix.CoreHash);
                using (var reader = new ReplayReader(path))
                {
                    int steps = 0; while (reader.ReadNext() is { } record) if (record is ReplayStepRecord) steps++;
                    Equal(12, steps);
                }
                registration!.Subscription.Dispose();
            }),
            ("Slow Observer overflow does not alter Core and latest snapshot remains correct", async () =>
            {
                var writer = new CaptureWriter(); ObserverHub? hub = null; ObserverRegistration? registration = null;
                RunResult result = await Run("success", new()
                {
                    ReplayWriter = writer,
                    ObserverReady = owner => { hub = owner; registration = owner.Register(1); }
                });
                Gone(result); Equal("success", result.Kind); Equal(SuccessHash, result.CoreHash); Equal(13L, result.Tick);
                Require(result.Error is null && registration!.Subscription.RequiresResync, "Slow subscription was not detached.");
                SnapshotMessage current = hub!.CurrentSnapshot(); Equal(13L, current.Tick); Equal(27L, current.BaseSeq);
                Equal(MissionPhaseDto.Succeeded, current.State.MissionPhase); Equal(EpisodeKindDto.Success, current.State.Episode!.Kind);
                Equal(AgentStatusDto.Stopped, current.AgentStatus);
                Require(registration!.Subscription.Reader.TryRead(out var queued) && queued is AgentStatusMessage { Seq: 1 }, "Slow queue overwrote its first envelope.");
                await Throws<ObserverResyncException>(() => registration!.Subscription.Reader.Completion);
                string path = Save(writer); Require(ReplayService.Verify(path).Valid, "Slow Observer damaged recording.");
                VisualState? replayed = null;
                ReplayService.Export(path, message =>
                { if (message is SnapshotMessage snapshot) replayed = VisualState.FromSnapshot(snapshot); else replayed!.Apply(message); });
                Equal(ObserverCodec.Encode(current), ObserverCodec.Encode(replayed!.Snapshot())); Equal(1, writer.DisposeCount);
                Equal("fault-agent", ((ReplayRunHeader)writer.Records[0]).AgentName); registration!.Subscription.Dispose();
            })
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M4 Integration: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private sealed class CaptureWriter(int? failAt = null) : IReplayWriter
    {
        public List<object> Records { get; } = [];
        public int Attempts { get; private set; }
        public int DisposeCount { get; private set; }
        private bool _failed;
        public ValueTask AppendAsync(object record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (_failed || Attempts == failAt)
            {
                _failed = true;
                throw new IOException("Injected authority append failure.");
            }
            Records.Add(ReplayCodec.Parse(ReplayCodec.Encode(record)));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static void Gone(RunResult result)
    {
        Require(result.AgentProcessId is not null, "No Agent PID to verify cleanup.");
        try { using var process = Process.GetProcessById(result.AgentProcessId!.Value); Require(process.HasExited, "Agent process remains alive."); }
        catch (ArgumentException) { }
    }
}
