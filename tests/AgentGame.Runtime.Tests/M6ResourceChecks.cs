using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Agents;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

/// <summary>
/// M6 layered performance baseline plus the bounded-queue evidence.
/// Runs real episodes (GameRunner + Python fixture or the CLI), measures each layer separately,
/// guards regressions with deliberately loose ceilings (never flaky on a shared machine), and writes
/// the real numbers to artifacts/m6-perf/report.json. Nothing here is registered in the main runner;
/// it is driven by a throwaway host project for self-verification.
/// </summary>
internal static class M6ResourceChecks
{
    private const double BytesPerStepCeiling = 8192;      // stated, measured bound for check 2
    private const double SeedsCeilingSeconds = 20;        // (a) 100 seeds Generate + Validate
    private const double StepsCeilingSeconds = 5;         // (b) 200 Core steps with Observe + Capture
    private const double HashCeilingSeconds = 5;          // (c) 1000 StateEncoding.Hash calls
    private const double VerifyCeilingSeconds = 10;       // (d) ReplayService.Verify of the recorded file
    private const double CliCeilingSeconds = 30;          // (e) one full CLI run with the Python agent

    public static async Task<int> RunAsync(string root)
    {
        var report = new Report();
        string directory = Path.Combine(root, "artifacts", "m6-perf");
        Directory.CreateDirectory(directory);

        string fixture = Path.Combine(root, "tests", "Fixtures", "Agents", "fault_agent.py");
        string cliDll = Path.Combine(root, "src", "AgentGame.Cli", "bin", "Release", "net10.0", "agent-game.dll");

        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Bounded slow subscriber queue is bounded, never stalls the run, and yields at most one envelope", async () =>
                await BoundedSlowSubscriberAsync(root, fixture, report)),
            ("Record growth is linear and non-duplicating (2N+3 lines, low bytes-per-step)", async () =>
                await RecordGrowthAsync(root, fixture, report)),
            ("Layer timings stay inside loose regression ceilings and are reported", async () =>
                await LayerTimingsAsync(root, fixture, cliDll, report)),
            ("Repeated identical episodes accumulate no state across runs", async () =>
                await RepeatedEpisodesAsync(root, fixture))
        };

        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }

        // Write the report regardless of pass/fail so the real numbers are always available.
        string reportPath = WriteReport(report, directory);
        Console.WriteLine($"M6 report: {reportPath}");
        Console.WriteLine($"M6 Resource: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    // ---- Check 1 ---------------------------------------------------------------

    private static async Task BoundedSlowSubscriberAsync(string root, string fixture, Report report)
    {
        ScenarioDto scene = ReadWithMaxTicks(root, 64);
        ObserverHub? hub = null;
        ObserverRegistration? registration = null;
        RunResult result = await RunEpisodeAsync(fixture, scene, new RunOptions
        {
            ObserverReady = owner => { hub = owner; registration = owner.Register(1); }
        });
        Equal("turn_limit", result.Kind);
        Equal(64L, result.Tick);
        Require(result.Error is null, "A non-consuming subscriber disturbed the run: " + (result.Error?.Detail ?? ""));
        Require(registration is not null && registration.Subscription.RequiresResync,
            "Capacity-one slow subscription was not detached / did not request resync.");
        SnapshotMessage current = hub!.CurrentSnapshot();
        Equal(64L, current.Tick);
        Equal(EpisodeKindDto.TurnLimit, current.State.Episode!.Kind);
        // Drain: the bounded queue can hold at most its capacity (1), so at most one envelope.
        int drained = 0;
        while (registration!.Subscription.Reader.TryRead(out _)) drained++;
        Require(drained <= 1, $"Bounded queue released {drained} envelopes from a capacity-1 subscriber.");
        // The overflow must surface as a resync fault, never silently drop.
        await ThrowsAsync<ObserverResyncException>(() => registration.Subscription.Reader.Completion);
        registration.Subscription.Dispose();
        report.BoundedQueue.Kind = result.Kind;
        report.BoundedQueue.Tick = result.Tick;
        report.BoundedQueue.RequiresResync = true;
        report.BoundedQueue.DrainedEnvelopes = drained;
    }

    // ---- Check 2 ---------------------------------------------------------------

    private static async Task RecordGrowthAsync(string root, string fixture, Report report)
    {
        ScenarioDto scene = ReadWithMaxTicks(root, 64);
        string record = Path.Combine(root, "artifacts", "m6-perf", "growth-" + Guid.NewGuid().ToString("N") + ".jsonl");
        RunResult result;
        try
        {
            result = await RunEpisodeAsync(fixture, scene, new RunOptions { RecordPath = record });
        }
        catch
        {
            TryDelete(record);
            throw;
        }
        Equal("turn_limit", result.Kind);
        Equal(64L, result.Tick);
        Require(result.Error is null, "Recorded run failed: " + (result.Error?.Detail ?? ""));
        long byteLength = new FileInfo(record).Length;

        // Step count from the record must equal the committed tick count (no duplicates).
        int steps = 0;
        using (var reader = new ReplayReader(record))
        {
            while (reader.ReadNext() is { } item)
            {
                if (item is ReplayStepRecord) steps++;
            }
        }
        Equal((int)result.Tick, steps);
        Equal("completed", ReadStatus(record));

        // Structure: header + N waiting statuses + N steps + closing status + footer = 2N+3 lines.
        long lineCount = FileCountLines(record);
        Equal(2 * result.Tick + 3, lineCount);

        double bytesPerStep = byteLength / (double)result.Tick;
        report.Growth.CommittedTicks = result.Tick;
        report.Growth.StepRecords = steps;
        report.Growth.TotalLines = lineCount;
        report.Growth.Bytes = byteLength;
        report.Growth.BytesPerStep = Math.Round(bytesPerStep, 1);
        report.Growth.BytesPerStepCeiling = BytesPerStepCeiling;
        Require(bytesPerStep <= BytesPerStepCeiling,
            $"Bytes-per-step {bytesPerStep:F1} exceeds stated ceiling {BytesPerStepCeiling}.");
        Console.WriteLine($"  record: N={result.Tick} steps={steps} lines={lineCount} bytes={byteLength} bytes/step={bytesPerStep:F1}");

        report.RecordFile = record;
    }

    // ---- Check 3 ---------------------------------------------------------------

    private static async Task LayerTimingsAsync(string root, string fixture, string cliDll, Report report)
    {
        // (a) 100 seeds Generate + Validate
        var sw = Stopwatch.StartNew();
        int validated = 0;
        for (ulong seed = 0; seed < 100; seed++)
        {
            ScenarioDto dto = ScenarioService.Generate(seed);
            Require(ScenarioService.Validate(dto).Valid, "Seed " + seed + " failed validation.");
            validated++;
        }
        double seedsSeconds = sw.Elapsed.TotalSeconds;
        report.Timings.SeedGenerateValidateSeconds = Math.Round(seedsSeconds, 3);
        report.Timings.Seeds = validated;
        Require(seedsSeconds < SeedsCeilingSeconds, $"100 seeds took {seedsSeconds:F2}s >= {SeedsCeilingSeconds}s ceiling.");
        Console.WriteLine($"  (a) Generate+Validate 100 seeds: {seedsSeconds:F3}s");

        // (b) 200 Core steps with Observe + Capture
        ScenarioDto baseScene = ScenarioService.Read(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json"));
        Game game = Game.Create(ToCoreScenario(baseScene with { MaxTicks = 512 }));
        sw.Restart();
        for (int i = 0; i < 200; i++)
        {
            _ = game.Step(GameAction.Wait());
            _ = game.Capture();
            _ = game.Observe();
        }
        double stepsSeconds = sw.Elapsed.TotalSeconds;
        report.Timings.CoreSteps = 200;
        report.Timings.CoreStepsSeconds = Math.Round(stepsSeconds, 6);
        report.Timings.CoreStepsMicros = (long)Math.Round(stepsSeconds * 1_000_000);
        Require(stepsSeconds < StepsCeilingSeconds, $"200 Core steps took {stepsSeconds:F2}s >= {StepsCeilingSeconds}s ceiling.");
        Console.WriteLine($"  (b) 200 Core steps (Step+Capture+Observe): {stepsSeconds * 1_000_000:F0}us");

        // (c) 1000 StateEncoding.Hash calls
        CoreSnapshot snapshot = game.Capture();
        sw.Restart();
        for (int i = 0; i < 1000; i++) _ = StateEncoding.Hash(snapshot);
        double hashSeconds = sw.Elapsed.TotalSeconds;
        report.Timings.Hashes = 1000;
        report.Timings.HashSeconds = Math.Round(hashSeconds, 6);
        report.Timings.HashMicros = (long)Math.Round(hashSeconds * 1_000_000);
        Require(hashSeconds < HashCeilingSeconds, $"1000 hashes took {hashSeconds:F2}s >= {HashCeilingSeconds}s ceiling.");
        Console.WriteLine($"  (c) 1000 StateEncoding.Hash: {hashSeconds * 1_000_000:F0}us");

        // (d) ReplayService.Verify of the recorded file from check 2
        Require(report.RecordFile is not null, "No recorded file to verify; run a recording first.");
        sw.Restart();
        VerificationResult verified = ReplayService.Verify(report.RecordFile!);
        double verifySeconds = sw.Elapsed.TotalSeconds;
        Require(verified.Valid, "Recorded file failed verification: " + (verified.Error ?? "unknown"));
        report.Timings.VerifySeconds = Math.Round(verifySeconds, 3);
        Require(verifySeconds < VerifyCeilingSeconds, $"Verify took {verifySeconds:F2}s >= {VerifyCeilingSeconds}s ceiling.");
        Console.WriteLine($"  (d) ReplayService.Verify: {verifySeconds:F3}s");

        // (e) one full CLI run with the Python agent (child process, wall clock)
        string cliScene = Path.Combine(root, "artifacts", "m6-perf", "cli-" + Guid.NewGuid().ToString("N") + ".json");
        ScenarioService.Write(cliScene, ReadWithMaxTicks(root, 32));
        string cliRecord = Path.Combine(root, "artifacts", "m6-perf", "cli-" + Guid.NewGuid().ToString("N") + ".jsonl");
        string python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        sw.Restart();
        CliResult cli = await RunCliAsync(cliDll,
            ["run", "--scenario", cliScene, "--headless", "--record", cliRecord, "--", python, "-u", fixture, "--mode", "wait"]);
        double cliSeconds = sw.Elapsed.TotalSeconds;
        Require(cli.ExitCode == 0, "CLI run exited " + cli.ExitCode + ": " + cli.Stderr.Trim());
        Require(File.Exists(cliRecord), "CLI produced no record file.");
        report.Timings.CliWallSeconds = Math.Round(cliSeconds, 3);
        Require(cliSeconds < CliCeilingSeconds, $"CLI run took {cliSeconds:F2}s >= {CliCeilingSeconds}s ceiling.");
        Console.WriteLine($"  (e) full CLI run (Python agent): {cliSeconds:F3}s (exit {cli.ExitCode})");
    }

    // ---- Check 4 ---------------------------------------------------------------

    private static async Task RepeatedEpisodesAsync(string root, string fixture)
    {
        const int runs = 5;
        string? priorSnapshot = null;
        int? priorLines = null;
        for (int i = 0; i < runs; i++)
        {
            // max_ticks must exceed the scenario's reference length (13) so the scenario validates,
            // while the wait agent still only ever reaches the turn limit.
            ScenarioDto scene = ReadWithMaxTicks(root, 16);
            string record = Path.Combine(root, "artifacts", "m6-perf", "repeat-" + Guid.NewGuid().ToString("N") + ".jsonl");
            ObserverHub? hub = null;
            RunResult result = await RunEpisodeAsync(fixture, scene,
                new RunOptions { RecordPath = record, RunId = "m6-repeat", ObserverReady = owner => hub = owner });
            Equal("turn_limit", result.Kind);
            Equal(16L, result.Tick);
            string snapshot = ObserverCodec.Encode(hub!.CurrentSnapshot());
            long lines = FileCountLines(record);
            if (priorSnapshot is not null)
            {
                Equal(priorSnapshot, snapshot);
                Equal(priorLines!.Value, lines);
            }
            priorSnapshot = snapshot;
            priorLines = (int)lines;
        }
    }

    // ---- Shared helpers -------------------------------------------------------

    private static ScenarioDto ReadWithMaxTicks(string root, int maxTicks)
    {
        ScenarioDto baseScene = ScenarioService.Read(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json"));
        return ScenarioCodec.Parse(ScenarioCodec.Encode(baseScene with { MaxTicks = maxTicks }));
    }

    private static Scenario ToCoreScenario(ScenarioDto dto) => new(dto.Rows,
        new Position(dto.Start.X, dto.Start.Y), new Position(dto.Exit.X, dto.Exit.Y),
        new Position(dto.Key.X, dto.Key.Y), new Position(dto.Door.X, dto.Door.Y),
        new Position(dto.Core.X, dto.Core.Y), dto.MaxTicks, dto.VisibilityRadius);

    private static async Task<RunResult> RunEpisodeAsync(string fixture, ScenarioDto scene, RunOptions options)
    {
        string python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        var command = new AgentCommand(python, ["-u", fixture, "--mode", "wait"]);
        var timeouts = new AgentTimeouts
        {
            Handshake = TimeSpan.FromSeconds(5), Decision = TimeSpan.FromSeconds(30),
            ShutdownGrace = TimeSpan.FromMilliseconds(150), KillWait = TimeSpan.FromSeconds(1)
        };
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await GameRunner.RunAsync(scene, command, timeouts, watchdog.Token, options)
            .WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static string ReadStatus(string record)
    {
        using var reader = new ReplayReader(record);
        while (reader.ReadNext() is { } item) { }
        return reader.Status;
    }

    private static long FileCountLines(string path)
    {
        Span<byte> buffer = stackalloc byte[64 * 1024];
        long count = 0;
        using var stream = File.OpenRead(path);
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            for (int i = 0; i < read; i++) if (buffer[i] == (byte)'\n') count++;
        }
        return count;
    }

    private static async Task<CliResult> RunCliAsync(string cliDll, string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add(cliDll);
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        Require(process.Start(), "Failed to start the CLI.");
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await stdout, await stderr);
    }

    // ---- Report ---------------------------------------------------------------

    private static string WriteReport(Report report, string directory)
    {
        string path = Path.Combine(directory, "report.json");
        var payload = new
        {
            check = "m6-resource",
            machine = Environment.OSVersion.ToString(),
            architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            sdk = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            bounded_queue = new
            {
                kind = report.BoundedQueue.Kind,
                tick = report.BoundedQueue.Tick,
                requires_resync = report.BoundedQueue.RequiresResync,
                drained_envelopes = report.BoundedQueue.DrainedEnvelopes
            },
            record_growth = new
            {
                committed_ticks = report.Growth.CommittedTicks,
                step_records = report.Growth.StepRecords,
                total_lines = report.Growth.TotalLines,
                bytes = report.Growth.Bytes,
                bytes_per_step = report.Growth.BytesPerStep,
                bytes_per_step_ceiling = report.Growth.BytesPerStepCeiling
            },
            timings = new
            {
                // Core steps and hashing are sub-millisecond on this fixture, so they are also
                // recorded in microseconds: rounding seconds to milliseconds would report 0 and
                // make the baseline useless for regression comparison.
                seed_generate_validate_seconds = report.Timings.SeedGenerateValidateSeconds,
                seeds = report.Timings.Seeds,
                core_steps = report.Timings.CoreSteps,
                core_steps_seconds = report.Timings.CoreStepsSeconds,
                core_steps_micros = report.Timings.CoreStepsMicros,
                hash_calls = report.Timings.Hashes,
                hash_seconds = report.Timings.HashSeconds,
                hash_micros = report.Timings.HashMicros,
                verify_seconds = report.Timings.VerifySeconds,
                cli_run_wall_seconds = report.Timings.CliWallSeconds
            },
            ceilings_seconds = new
            {
                seeds = SeedsCeilingSeconds,
                core_steps = StepsCeilingSeconds,
                hash = HashCeilingSeconds,
                verify = VerifyCeilingSeconds,
                cli_run = CliCeilingSeconds
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private sealed class Report
    {
        public Bounded BoundedQueue { get; } = new();
        public GrowthData Growth { get; } = new();
        public TimingsData Timings { get; } = new();
        public string? RecordFile { get; set; }
        public sealed class Bounded
        {
            public string Kind = ""; public long Tick; public bool RequiresResync; public int DrainedEnvelopes;
        }
        public sealed class GrowthData
        {
            public long CommittedTicks; public int StepRecords; public long TotalLines;
            public long Bytes; public double BytesPerStep; public double BytesPerStepCeiling;
        }
        public sealed class TimingsData
        {
            public double SeedGenerateValidateSeconds; public int Seeds;
            public int CoreSteps; public double CoreStepsSeconds; public long CoreStepsMicros;
            public int Hashes; public double HashSeconds; public long HashMicros;
            public double VerifySeconds; public double CliWallSeconds;
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void TryDelete(string path)
    { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}