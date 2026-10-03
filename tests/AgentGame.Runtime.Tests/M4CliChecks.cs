using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Agents;
using AgentGame.Runtime.Replay;

/// <summary>
/// Black-box contract checks for the M4 command surface. Every check launches a real
/// <c>dotnet &lt;cli dll&gt;</c> child process through <see cref="ProcessStartInfo.ArgumentList"/>,
/// gives it an explicit deadline and kills its whole tree on expiry. Nothing here calls
/// dotnet test, reads credentials, uses the network or writes outside the per-run temporary
/// directory below <c>artifacts/m4-cli-tests</c>.
/// </summary>
internal static class M4CliChecks
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(2);

    private static string _root = "";
    private static string _cli = "";
    private static string _directory = "";
    private static string _python = "";
    private static string _fixture = "";
    private static string _fixtureScenario = "";
    private static string? _waitScenario;

    public static async Task<int> RunAsync(string root)
    {
        _root = root;
        _cli = Path.Combine(root, "src", "AgentGame.Cli", "bin", "Release", "net10.0", "agent-game.dll");
        _directory = Path.Combine(root, "artifacts", "m4-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        _fixture = Path.Combine(root, "tests", "Fixtures", "Agents", "fault_agent.py");
        _fixtureScenario = Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json");

        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Generated scenario runs records replays and verifies end to end", EndToEndAsync),
            ("Tampered action and core hash fail verification at the first changed step", TamperAsync),
            ("Footer removal replays the prefix but never reports agreement", TruncatedAsync),
            ("Middle line corruption stops with a position", CorruptionAsync),
            ("Headless stdout carries only JSON summaries", StreamsAsync),
            ("Unwritable record path reports a record I/O error without publishing steps", RecordFailureAsync),
            ("Blocked observer stdout cannot change a fixed-action run", BlockedObserverAsync),
            ("Headless contradicts observer streaming and is rejected as usage", HeadlessConflictAsync)
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M4 CLI: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    // 1) scenario generate -> scenario validate -> run --record -> replay -> verify.
    private static async Task EndToEndAsync()
    {
        Require(File.Exists(_cli), "CLI assembly not found at " + _cli + ".");
        string scenario = Path.Combine(_directory, "generated.json");
        RunCliResult generate = await RunCliAsync(
            ["scenario", "generate", "--seed", "42", "--out", scenario, "--max-ticks", "64"], captureStdout: true);
        Require(generate.ExitCode == 0, "scenario generate exited " + generate.ExitCode + ": " + generate.Stderr.Trim());
        JsonObject generated = OnlyJsonLine(generate, "scenario generate");
        Require(generated["valid"]?.GetValue<bool>() == true, "Generated scenario reports invalid: " + generated.ToJsonString());
        Require(generated["error"] is null, "Generated scenario reported an error: " + generated["error"]?.ToJsonString());
        Require(generated["reference_length"]!.GetValue<int>() is >= 1 and <= 128, "Reference length is out of budget.");
        Require(generated["initial_hash"]!.GetValue<string>().Length == 64, "Generated initial hash is not 64 characters.");
        Require(File.Exists(scenario), "scenario generate created no file.");

        RunCliResult validate = await RunCliAsync(["scenario", "validate", scenario], captureStdout: true);
        Require(validate.ExitCode == 0, "scenario validate exited " + validate.ExitCode + ": " + validate.Stderr.Trim());
        JsonObject validated = OnlyJsonLine(validate, "scenario validate");
        Require(validated["valid"]?.GetValue<bool>() == true, "Validation rejected the generated scenario.");
        Equal(generated["initial_hash"]!.GetValue<string>(), validated["initial_hash"]!.GetValue<string>());

        string record = Path.Combine(_directory, "run.jsonl");
        RunCliResult run = await RunCliAsync(
            ["run", "--scenario", scenario, "--headless", "--record", record, "--", _python, "-u", _fixture, "--mode", "wait"],
            captureStdout: true);
        Require(run.ExitCode == 0, "run exited " + run.ExitCode + ": " + run.Stderr.Trim());
        JsonObject summary = OnlyJsonLine(run, "run");
        string kind = summary["kind"]!.GetValue<string>();
        Require(kind is "success" or "turn_limit", "Run did not finish a rule episode: " + kind);
        Require(summary["error"] is null, "Successful run reported error: " + summary["error"]?.ToJsonString());
        long tick = summary["tick"]!.GetValue<long>();
        Require(tick >= 1, "Expected at least one committed turn.");
        Equal("run/1", summary["format"]!.GetValue<string>());

        List<StepFact> steps = ReadSteps(record);
        Equal((int)tick, steps.Count);
        Equal((int)tick, steps[^1].Tick);
        Require(steps.All(step => step.CoreHash.Length == 64), "A committed step lost its core hash.");

        // Offline playback and verification must not need a runnable Agent at all: point the
        // Agent interpreter at a path that does not exist and require both commands to pass.
        string offlinePython = Path.Combine(_directory, "not-an-interpreter-" + Guid.NewGuid().ToString("N"));
        RunCliResult replay = await RunCliAsync(["replay", record], null, captureStdout: true,
            environment: new() { ["AGENT_GAME_PYTHON"] = offlinePython });
        Require(replay.ExitCode == 0, "replay exited " + replay.ExitCode + ": " + replay.Stderr.Trim());
        List<string> played = ParseLines(replay.Stdout);
        List<JsonObject> envelopes = played.Select(line => TryParseObject(line)
            ?? throw new Exception("replay emitted a non-JSON line: " + line)).ToList();
        Equal("snapshot", envelopes[0]["type"]!.GetValue<string>());
        // The export replays every committed envelope: one step_batch and one waiting
        // agent_status per committed turn, plus the closing agent_status.
        Equal(steps.Count, envelopes.Count(node => node["type"]!.GetValue<string>() == "step_batch"));
        Equal(steps.Count + 1, envelopes.Count(node => node["type"]!.GetValue<string>() == "agent_status"));
        Require(envelopes.Skip(1).All(node => node["type"]!.GetValue<string>() is "agent_status" or "step_batch"),
            "replay emitted an unexpected envelope type.");
        List<string> replayDiagnostics = ParseLines(replay.Stderr);
        Equal(1, replayDiagnostics.Count);
        JsonObject replaySummary = ParseObject(replayDiagnostics[0], "replay summary");
        Equal("completed", replaySummary["status"]!.GetValue<string>());
        Equal(tick, replaySummary["last_tick"]!.GetValue<long>());

        RunCliResult verify = await RunCliAsync(["verify", record], null, captureStdout: true,
            environment: new() { ["AGENT_GAME_PYTHON"] = offlinePython });
        Require(verify.ExitCode == 0, "verify exited " + verify.ExitCode + ": " + verify.Stderr.Trim());
        JsonObject verified = OnlyJsonLine(verify, "verify");
        Require(verified["valid"]!.GetValue<bool>(), "verify did not report agreement: " + verified.ToJsonString());
        Equal("completed", verified["status"]!.GetValue<string>());
        Equal(tick, verified["tick"]!.GetValue<long>());
        Equal(summary["core_hash"]!.GetValue<string>(), verified["core_hash"]!.GetValue<string>());
        Require(verified["error"] is null, "Verify reported an error while agreeing.");
    }

    // 2) Tampering a middle step must be caught at exactly that step.
    private static async Task TamperAsync()
    {
        string record = await RecordWaitAsync("tamper-source");
        List<StepFact> steps = ReadSteps(record);
        Require(steps.Count >= 4, "Tamper check needs at least four committed steps, found " + steps.Count + ".");
        StepFact target = steps[2];
        List<string> lines = ReadLines(record);
        JsonObject step = ParseObject(lines[target.Line - 1], "step record");

        JsonObject actionTamper = (JsonObject)JsonNode.Parse(step.ToJsonString())!;
        JsonObject action = actionTamper["action"]!.AsObject();
        string original = action.ToJsonString();
        // The wait fixture answers wait every turn, so "tamper to wait" would be a no-op.
        // Change the action kind, or the direction when the recorded action is directional.
        if (action["type"]!.GetValue<string>() == "wait")
        {
            action["type"] = "move";
            action["direction"] = "north";
        }
        else
        {
            action["direction"] = action["direction"]!.GetValue<string>() == "north" ? "south" : "north";
        }
        Require(action.ToJsonString() != original, "Tampering left the recorded action unchanged.");
        string actionPath = WriteLines(Replace(lines, target.Line, actionTamper));        await ExpectFirstDivergenceAsync(actionPath, record, target, "action");

        JsonObject hashTamper = (JsonObject)JsonNode.Parse(step.ToJsonString())!;
        string hash = hashTamper["core_hash"]!.GetValue<string>();
        hashTamper["core_hash"] = new string(hash[0] == 'a' ? 'b' : 'a', hash.Length);
        string hashPath = WriteLines(Replace(lines, target.Line, hashTamper));
        await ExpectFirstDivergenceAsync(hashPath, record, target, "core_hash");
    }

    // 3) A replay whose footer was removed still plays the whole committed prefix but must
    //    never claim that the recording agrees.
    private static async Task TruncatedAsync()
    {
        string record = await RecordWaitAsync("footer-source");
        List<string> lines = ReadLines(record);
        Require(lines.Count >= 4, "Footer check needs a multi-record replay.");
        Require(ParseObject(lines[^1], "footer")["type"]!.GetValue<string>() == "run_footer", "Last record is not the footer.");
        string truncated = WriteLines(lines.Take(lines.Count - 1).ToList());
        List<StepFact> prefix = ReadSteps(truncated);
        Equal(ReadSteps(record).Count, prefix.Count);

        RunCliResult replay = await RunCliAsync(["replay", truncated], null, captureStdout: true);
        Equal(1, replay.ExitCode);
        List<string> played = ParseLines(replay.Stdout);
        List<JsonObject> playedEnvelopes = played.Select(line => TryParseObject(line)
            ?? throw new Exception("replay emitted a non-JSON line: " + line)).ToList();
        Equal("snapshot", playedEnvelopes[0]["type"]!.GetValue<string>());
        Equal(prefix.Count, playedEnvelopes.Count(node => node["type"]!.GetValue<string>() == "step_batch"));
        Equal(prefix.Count + 1, playedEnvelopes.Count(node => node["type"]!.GetValue<string>() == "agent_status"));
        List<string> diagnostics = ParseLines(replay.Stderr);
        Equal(1, diagnostics.Count);
        JsonObject summary = ParseObject(diagnostics[0], "truncated replay summary");
        Equal("incomplete", summary["status"]!.GetValue<string>());
        Equal(prefix[^1].Tick, summary["last_tick"]!.GetValue<long>());

        RunCliResult verify = await RunCliAsync(["verify", truncated], null, captureStdout: true);
        Equal(1, verify.ExitCode);
        JsonObject result = OnlyJsonLine(verify, "verify");
        Require(!result["valid"]!.GetValue<bool>(), "Verify agreed with a truncated recording.");
        Equal("incomplete", result["status"]!.GetValue<string>());
        Equal("incomplete", result["error"]!.GetValue<string>());
        Require(result.ToJsonString().Contains("\"incomplete\"", StringComparison.Ordinal),
            "Verify never reported an incomplete or unverifiable state.");
    }

    // 4) A corrupted middle line must stop at its position, not silently truncate.
    private static async Task CorruptionAsync()
    {
        string record = await RecordWaitAsync("corruption-source");
        List<StepFact> steps = ReadSteps(record);
        Require(steps.Count >= 4, "Corruption check needs at least four committed steps.");
        List<string> lines = ReadLines(record);
        int corruptLine = steps[2].Line - 1;
        Require(corruptLine >= 3, "Corruption must sit after the header and the first committed step.");
        lines[corruptLine - 1] = "{\"type\":\"step_record\",\"seq\":";
        string corrupt = WriteLines(lines);

        RunCliResult replay = await RunCliAsync(["replay", corrupt], null, captureStdout: true);
        Equal(1, replay.ExitCode);
        // Playback streams what it has already read, so the valid prefix must be exported
        // before the reader stops at the corrupted line (header snapshot plus every record
        // that precedes it) and nothing from that line onwards.
        List<JsonObject> emitted = ParseLines(replay.Stdout).Select(line => TryParseObject(line)
            ?? throw new Exception("corrupt replay emitted a non-JSON line: " + line)).ToList();
        Equal(corruptLine - 1, emitted.Count);
        Equal("snapshot", emitted[0]["type"]!.GetValue<string>());
        JsonObject summary = ParseObject(ParseLines(replay.Stderr).Single(), "corrupt replay summary");
        Equal("invalid_record", summary["error"]!.GetValue<string>());
        Equal(corruptLine, summary["line"]!.GetValue<int>());

        RunCliResult verify = await RunCliAsync(["verify", corrupt], null, captureStdout: true);
        Equal(1, verify.ExitCode);
        JsonObject verified = OnlyJsonLine(verify, "verify");
        Require(!verified["valid"]!.GetValue<bool>(), "Verify accepted a corrupted middle line.");
        Equal(corruptLine, verified["line"]!.GetValue<int>());
    }

    // 5) Machine mode: one JSON line per stdout record, diagnostics only on stderr.
    private static async Task StreamsAsync()
    {
        string record = Path.Combine(_directory, "streams.jsonl");
        RunCliResult run = await RunCliAsync(
            ["run", "--scenario", _fixtureScenario, "--headless", "--record", record, "--", _python, "-u", _fixture, "--mode", "success"],
            captureStdout: true);
        Equal(0, run.ExitCode);
        List<string> lines = ParseLines(run.Stdout);
        Equal(1, lines.Count);
        Require(lines.All(line => IsJsonObject(line)), "Headless stdout carried a non-JSON line.");
        JsonObject summary = ParseObject(lines[0], "headless summary");
        Equal("run/1", summary["format"]!.GetValue<string>());
        Equal("success", summary["kind"]!.GetValue<string>());
        Equal(13L, summary["tick"]!.GetValue<long>());
        Require(summary["error"] is null, "Successful headless run reported an error.");

        JsonObject verified = ParseObject(ParseLines((await RunCliAsync(
            ["verify", record], null, captureStdout: true)).Stdout).Single(), "verify");
        Require(verified["valid"]!.GetValue<bool>(), "Recorded headless run failed verification.");
        Equal(summary["core_hash"]!.GetValue<string>(), verified["core_hash"]!.GetValue<string>());
    }

    // 6) An unwritable --record path is a record I/O error, and no step may be published
    //    that the authority did not record.
    private static async Task RecordFailureAsync()
    {
        string blocked = Path.Combine(_directory, "record-container");
        Directory.CreateDirectory(blocked);
        string[] before = Directory.GetFileSystemEntries(blocked);
        RunCliResult run = await RunCliAsync(
            ["run", "--scenario", _fixtureScenario, "--headless", "--record", blocked, "--", _python, "-u", _fixture, "--mode", "success"],
            captureStdout: true);
        Equal(1, run.ExitCode);

        bool declared = false;
        List<string> stdout = ParseLines(run.Stdout);
        Require(stdout.Count <= 1, "A failed recording published more than one summary line.");
        long published = 0L;
        if (stdout.Count == 1)
        {
            Require(IsJsonObject(stdout[0]), "Failed run stdout is not JSON.");
            JsonObject summary = ParseObject(stdout[0], "failed run summary");
            Equal("execution_error", summary["kind"]!.GetValue<string>());
            JsonObject failure = summary["error"]?.AsObject() ?? throw new Exception("Failed run reported no error.");
            Equal("record_error", failure["code"]!.GetValue<string>());
            published = summary["tick"]?.GetValue<long>() ?? 0L;
            Require(summary["last_action"] is null, "A step that was never recorded became the last action.");
            Require(summary["last_request_id"] is null, "A step that was never recorded kept its request id.");
            declared = true;
        }
        string[] after = Directory.GetFileSystemEntries(blocked);
        Exact(before, after);
        if (!declared)
        {
            JsonObject failure = ParseObject(ParseLines(run.Stderr)
                .FirstOrDefault(line => TryParseObject(line) is { } node && node["error"] is not null)
                ?? throw new Exception("Record failure produced neither a run summary nor an error object."), "record failure");
            Require(failure["error"]!.GetValue<string>().Length > 0, "Record failure carried an empty message.");
        }
        // The only replay sink named by this run is unwritable, so the published step count
        // cannot exceed the number of committed steps, which is zero here.
        Equal(0L, published);
    }

    // 7) A blocked stdout pipe (slow Observer) must not perturb the rule outcome.
    private static async Task BlockedObserverAsync()
    {
        string baseline = Path.Combine(_directory, "baseline.jsonl");
        RunCliResult plain = await RunCliAsync(
            ["run", "--scenario", _fixtureScenario, "--headless", "--record", baseline, "--", _python, "-u", _fixture, "--mode", "success"],
            captureStdout: true);
        Equal(0, plain.ExitCode);
        JsonObject baselineSummary = OnlyJsonLine(plain, "baseline run");

        string blockedRecord = Path.Combine(_directory, "blocked.jsonl");
        var clock = Stopwatch.StartNew();
        RunCliResult blocked = await RunCliAsync(
            ["run", "--scenario", _fixtureScenario, "--observer-stdout", "--record", blockedRecord, "--", _python, "-u", _fixture, "--mode", "success"],
            captureStdout: false);
        clock.Stop();
        Require(!blocked.TimedOut, "A blocked observer stdout hung the run past " + CliTimeout.TotalSeconds + " seconds.");
        Require(clock.Elapsed < TimeSpan.FromSeconds(90), "Blocked observer run took " + clock.Elapsed.TotalSeconds + " seconds.");
        Equal(0, blocked.ExitCode);
        JsonObject blockedSummary = ParseLines(blocked.Stderr)
            .Select(line => JsonNode.Parse(line))
            .OfType<JsonObject>()
            .FirstOrDefault(node => node["format"]?.GetValue<string>() == "run/1")
            ?? throw new Exception("Blocked observer run wrote no run/1 summary to stderr.");
        Equal(baselineSummary["kind"]!.GetValue<string>(), blockedSummary["kind"]!.GetValue<string>());
        Equal(baselineSummary["tick"]!.GetValue<long>(), blockedSummary["tick"]!.GetValue<long>());
        Equal(baselineSummary["core_hash"]!.GetValue<string>(), blockedSummary["core_hash"]!.GetValue<string>());
        Require(blockedSummary["error"] is null, "Blocked observer changed the execution result.");

        List<StepFact> expected = ReadSteps(baseline);
        List<StepFact> actual = ReadSteps(blockedRecord);
        Require(expected.Count >= 13, "Fixed-action baseline recorded only " + expected.Count + " steps.");
        Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Equal(expected[index].Tick, actual[index].Tick);
            Equal(expected[index].CoreHash, actual[index].CoreHash);
            Equal(expected[index].Action, actual[index].Action);
        }
        Require(File.Exists(baseline) && File.Exists(blockedRecord), "A recording file is missing.");
    }

    private static async Task ExpectFirstDivergenceAsync(string tamperedPath, string record, StepFact target, string field)
    {
        List<string> lines = ReadLines(record);
        List<StepFact> steps = ReadSteps(tamperedPath);
        Equal(ReadSteps(record).Count, steps.Count);
        Equal(target.Line, steps[2].Line);
        int divergent = FirstDivergentStep(tamperedPath);
        Require(divergent >= 0, "The tampered " + field + " did not diverge from the recorded prefix.");
        Equal(3, divergent + 1);
        int line = ReadLines(record)[..target.Line].Select(text => ParseObject(text, "record line"))
            .Count(node => node["type"]?.GetValue<string>() == "step_record");

        RunCliResult verify = await RunCliAsync(["verify", tamperedPath], null, captureStdout: true);
        Equal(1, verify.ExitCode);
        JsonObject result = OnlyJsonLine(verify, "verify");
        Require(!result["valid"]!.GetValue<bool>(), "Verify accepted a tampered " + field + ".");
        Require(result["error"] is not null, "Verify reported no reason for rejecting the tampered " + field + ".");
        int reported = result["line"]?.GetValue<int>() ?? throw new Exception("Verify reported no position for the tampered " + field + ".");
        Equal(target.Line, reported);
        Require(reported is > 0, "Verify reported an invalid position.");
        Equal(line, ReadSteps(tamperedPath).Count(step => step.Line <= reported));
        // verify points at the first divergent step: `line` is that record's position and
        // `tick` is the tick the step produced (not the last agreeing tick).
        Equal(target.Tick, result["tick"]?.GetValue<long>());
        Require(lines.Count == ReadLines(tamperedPath).Count, "Tampering changed the record count.");
    }

    /// <summary>Executes the file's own recorded actions on a fresh Core game and returns the
    /// zero-based index of the first step whose recorded hash disagrees, or -1 when the whole
    /// prefix agrees. This detects both a tampered action and a tampered hash, because the
    /// recorded hash of a tampered step no longer matches what its action now produces.</summary>
    private static int FirstDivergentStep(string replayPath)
    {
        List<StepFact> recorded = ReadSteps(replayPath);
        ReplayRunHeader header;
        Game game;
        using (var reader = new ReplayReader(replayPath))
        {
            header = reader.Header;
            game = Game.Create(ScenarioService.ToCoreScenario(header.Scenario));
            int index = 0;
            while (reader.ReadNext() is { } next)
            {
                if (next is not ReplayStepRecord step) continue;
                game.Step(ReplayService.ToCore(step.Action));
                if (index >= recorded.Count) return index;
                if (!string.Equals(StateEncoding.Hash(game.Capture()), recorded[index].CoreHash, StringComparison.Ordinal))
                    return index;
                index++;
            }
        }
        return -1;
    }

    private static async Task<string> RecordWaitAsync(string name)
    {
        // A shorter turn budget keeps the tamper, footer and corruption checks quick. Sixteen
        // still exceeds the fixture's 13-turn shortest task, so the runner keeps accepting it.
        if (_waitScenario is null)
        {
            JsonNode scene = JsonNode.Parse(File.ReadAllText(_fixtureScenario, new UTF8Encoding(false, true)))!;
            scene["max_ticks"] = 16;
            _waitScenario = Path.Combine(_directory, "wait-scenario.json");
            File.WriteAllText(_waitScenario, scene.ToJsonString(), new UTF8Encoding(false, true));
        }
        string path = Path.Combine(_directory, name + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        RunCliResult run = await RunCliAsync(
            ["run", "--scenario", _waitScenario, "--headless", "--record", path, "--", _python, "-u", _fixture, "--mode", "wait"],
            captureStdout: true);
        Require(run.ExitCode == 0, "Fixture recording exited " + run.ExitCode + ": " + run.Stderr.Trim());
        JsonObject summary = OnlyJsonLine(run, "fixture run");
        Equal("turn_limit", summary["kind"]!.GetValue<string>());
        return path;
    }

    // 8) --headless means "attach no terminal observer"; combining it with streaming is a
    //    usage error rather than a silently ignored flag.
    private static async Task HeadlessConflictAsync()
    {
        RunCliResult conflict = await RunCliAsync(
            ["run", "--scenario", _fixtureScenario, "--headless", "--observer-stdout", "--", _python, "-u", _fixture, "--mode", "wait"],
            captureStdout: true);
        Equal(2, conflict.ExitCode);
        Require(conflict.Stdout.Trim().Length == 0, "Contradictory flags still wrote stdout: " + conflict.Stdout.Trim());
        Require(conflict.Stderr.Contains("--headless cannot be combined with --observer-stdout", StringComparison.Ordinal),
            "The contradiction was not explained on stderr: '" + conflict.Stderr.Trim() + "'");
    }

    /// <summary>Runs the CLI with an exact argument list, an explicit deadline, and a full    /// process-tree kill on expiry. Output is captured through event readers, except when
    /// <paramref name="captureStdout"/> is false, where the reader deliberately never drains
    /// the pipe so that a blocked stdout can be exercised.</summary>
    private static async Task<RunCliResult> RunCliAsync(string[] arguments, AgentTimeouts? timeouts = null,
        bool captureStdout = true, Dictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root, UseShellExecute = false,
            RedirectStandardOutput = captureStdout, RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(_cli);
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            Require(process.Start() is true, "Failed to start the CLI process.");
            // Stdout is only attached to an asynchronous reader when this check may drain it.
            // With captureStdout:false the pipe is intentionally never read, so a blocked
            // Observer stdout is exercised instead of being silently consumed.
            if (captureStdout)
            {
                process.OutputDataReceived += (_, line) => { if (line.Data is not null) lock (stdout) stdout.AppendLine(line.Data); };
                process.BeginOutputReadLine();
            }
            process.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (stderr) stderr.AppendLine(line.Data); };
            process.BeginErrorReadLine();

            using var deadline = new CancellationTokenSource(CliTimeout);
            bool timedOut = false;
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }
            if (!timedOut)
            {
                // Flush the asynchronous readers; a leaked inherited handle must not hang the check.
                if (captureStdout) process.CancelOutputRead();
                process.CancelErrorRead();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }
            string captured, diagnostics;
            lock (stdout) captured = stdout.ToString();
            lock (stderr) diagnostics = stderr.ToString();
            if (timedOut) return new(-1, captured, diagnostics, true);
            Require(process.HasExited, "CLI process did not exit.");
            return new(process.ExitCode, captured, diagnostics, false);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    private static List<StepFact> ReadSteps(string path)
    {
        var steps = new List<StepFact>();
        using var reader = new ReplayReader(path);
        while (reader.ReadNext() is { } record)
        {
            if (record is not ReplayStepRecord step) continue;
            steps.Add(new StepFact(reader.LineNumber, step.Tick, step.CoreHash,
                ProtocolJson.EncodeLine(step.Action), ProtocolJson.EncodeLine(step.Outcome)));
        }
        return steps;
    }

    private static List<string> ReadLines(string path) =>
        [.. File.ReadAllLines(path, new UTF8Encoding(false, true)).Where(line => line.Length > 0)];

    private static string WriteLines(List<string> lines)
    {
        string path = Path.Combine(_directory, "edit-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false, true));
        return path;
    }

    private static List<string> Replace(List<string> lines, int lineNumber, JsonObject record)
    {
        Require(lineNumber >= 1 && lineNumber <= lines.Count, "Tamper line is outside the file.");
        var copy = new List<string>(lines);
        copy[lineNumber - 1] = record.ToJsonString();
        return copy;
    }

    private static List<string> ParseLines(string text) =>
        [.. text.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0)];

    private static bool IsJsonObject(string line)
    {
        try { return JsonNode.Parse(line) is JsonObject; }
        catch (JsonException) { return false; }
    }

    private static JsonObject? TryParseObject(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static JsonObject ParseObject(string line, string context)
    {
        try { return JsonNode.Parse(line) as JsonObject ?? throw new Exception(context + " is not a JSON object."); }
        catch (JsonException error) { throw new Exception(context + " is not valid JSON: " + error.Message); }
    }

    private static JsonObject OnlyJsonLine(RunCliResult result, string context)
    {
        List<string> lines = ParseLines(result.Stdout);
        Equal(1, lines.Count);
        return ParseObject(lines[0], context + " stdout");
    }

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Exact(string[] expected, string[] actual)
    {
        Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
            if (!string.Equals(expected[index], actual[index], StringComparison.Ordinal))
                throw new Exception($"Record directory changed: expected {expected[index]}, got {actual[index]}.");
    }

    private sealed record StepFact(int Line, long Tick, string CoreHash, string Action, string Outcome);
    private sealed record RunCliResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
}
