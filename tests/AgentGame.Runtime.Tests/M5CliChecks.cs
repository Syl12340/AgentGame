using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Black-box contract checks for the M5 human command surface (<c>play</c>) and for the
/// <c>run --tui</c> terminal mode. Every check launches a real <c>dotnet &lt;cli dll&gt;</c> child
/// process through <see cref="ProcessStartInfo.ArgumentList"/> with the repository root as its
/// working directory, gives it an explicit deadline and kills its whole tree on expiry.
/// Stream redirection is real: stdin comes from an empty file, both output pipes are captured, and
/// a byte-exact copy of stdout is kept in a file, so every terminal-dependent decision the CLI
/// makes is exercised instead of assumed.
/// None of these checks needs a Python interpreter: the human session has no external Agent, and
/// the rejected <c>--tui</c> commands never reach the Agent. Nothing here reads credentials,
/// touches the network, changes global configuration or writes outside the per-run temporary
/// directory below <c>artifacts/m5-cli-tests</c>.
/// </summary>
internal static class M5CliChecks
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The manual corridor's shortest task is exactly 13 turns (docs/core-rules.md).</summary>
    private const long TurnLimitTicks = 13;

    /// <summary>The prompt line the human UI must keep showing, keys and all.</summary>
    private const string Legend = "[arrows/WASD] move   [E then direction, or Shift+arrow] interact   "
        + "[space/P] pickup   [.] wait   [Q/Esc] quit";

    private static string _root = "";
    private static string _cli = "";
    private static string _directory = "";
    private static string _fixture = "";
    private static string? _turnLimitScenario;

    public static async Task<int> RunAsync(string root)
    {
        _root = root;
        _cli = Path.Combine(root, "src", "AgentGame.Cli", "bin", "Release", "net10.0", "agent-game.dll");
        _directory = Path.Combine(root, "artifacts", "m5-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _fixture = Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json");

        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("run --tui is rejected when stdout is redirected instead of drawing a frame", TuiRejectedAsync),
            ("run --tui contradicts --observer-stdout and --headless as usage errors", TuiConflictsAsync),
            ("play finishes a redirected human episode and records a verifiable replay", PlayRedirectedAsync),
            ("play rejects a missing scenario and an unknown option without writing stdout", PlayUsageAsync),
            ("play --plain keeps stdout and stderr free of ANSI escapes and prints the key legend", PlayNoAnsiAsync)
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M5 CLI: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    // 1) --tui renders a live map, so a redirected stdout means "no terminal" and must be refused
    //    before anything runs: no frame, no ANSI, no run/1 summary.
    private static async Task TuiRejectedAsync()
    {
        RequireCli();
        string stdout = Path.Combine(_directory, "tui-rejected-stdout.txt");
        RunCliResult run = await RunCliAsync(
            ["run", "--scenario", _fixture, "--tui", "--", "python", "-u", "agents/random_agent.py"],
            standardOutputPath: stdout);
        Equal(2, run.ExitCode);
        Require(run.Stderr.Contains("interactive terminal", StringComparison.Ordinal),
            "--tui redirection was not explained as a missing interactive terminal: '" + run.Stderr.Trim() + "'");
        string captured = ReadRedirectedText(stdout);
        Require(captured.Length == 0, "The rejected --tui run still wrote stdout: '" + captured.Trim() + "'");
        // A byte-level check, because an empty file and an "empty-looking" one are not the same thing.
        Require(ReadRedirectedBytes(stdout).Length == 0, "The rejected --tui run wrote stdout bytes.");
        Require(!HasEscape(captured), "The rejected --tui run emitted ANSI on stdout.");
        Require(!HasEscape(run.Stderr), "The rejected --tui run emitted ANSI on stderr.");
        Require(TryParseObject(captured) is null, "The rejected --tui run published a JSON summary on stdout.");
        Require(!run.TimedOut, "The rejected --tui run had to be killed after " + CliTimeout.TotalSeconds + " seconds.");
        // A usage error must fail before the scenario is even opened, so this run can never have
        // produced the shorter turn-limit scenario a later check writes.
        Require(!File.Exists(Path.Combine(_directory, "turn-limit-scenario.json")),
            "The rejected --tui run got as far as preparing the scenario.");
    }

    // 2) --tui owns the terminal, so it cannot be combined with a streaming or headless observer.
    //    Both contradictions are usage errors (exit 2) and must name the offending pair.
    private static async Task TuiConflictsAsync()
    {
        RequireCli();
        string streamStdout = Path.Combine(_directory, "tui-stream-stdout.txt");
        RunCliResult stream = await RunCliAsync(
            ["run", "--scenario", _fixture, "--tui", "--observer-stdout", "--", "python", "-u", "agents/random_agent.py"],
            standardOutputPath: streamStdout);
        Equal(2, stream.ExitCode);
        Require(ReadRedirectedText(streamStdout).Length == 0, "The --tui/--observer-stdout conflict still wrote stdout.");
        Require(TryParseObject(stream.Stderr) is null, "The --tui/--observer-stdout conflict published a JSON object on stderr.");
        Require(stream.Stderr.Contains("--tui", StringComparison.Ordinal)
            && stream.Stderr.Contains("--observer-stdout", StringComparison.Ordinal),
            "The --tui/--observer-stdout contradiction was not named on stderr: '" + stream.Stderr.Trim() + "'");

        string headlessStdout = Path.Combine(_directory, "tui-headless-stdout.txt");
        RunCliResult headless = await RunCliAsync(
            ["run", "--scenario", _fixture, "--tui", "--headless", "--", "python", "-u", "agents/random_agent.py"],
            standardOutputPath: headlessStdout);
        Equal(2, headless.ExitCode);
        Require(ReadRedirectedText(headlessStdout).Length == 0, "The --tui/--headless conflict still wrote stdout.");
        Require(headless.Stderr.Contains("--tui", StringComparison.Ordinal)
            && headless.Stderr.Contains("--headless", StringComparison.Ordinal),
            "The --tui/--headless contradiction was not named on stderr: '" + headless.Stderr.Trim() + "'");
    }

    // 3) A human episode with EOF on stdin, no terminal and no Agent process must still run to the
    //    rule end, record every committed turn and leave a recording that verifies offline.
    private static async Task PlayRedirectedAsync()
    {
        RequireCli();
        string scenario = TurnLimitScenario();
        string record = Path.Combine(_directory, "play-turned-limit.jsonl");
        string stdout = Path.Combine(_directory, "play-stdout.txt");
        string stdin = EmptyInput();
        RunCliResult play = await RunCliAsync(
            ["play", "--scenario", scenario, "--plain", "--record", record],
            standardInputPath: stdin, standardOutputPath: stdout);
        Require(play.ExitCode == 0, "play exited " + play.ExitCode + ": " + play.Stderr.Trim());

        string captured = ReadRedirectedText(stdout);
        List<string> lines = ParseLines(captured);
        Equal(1, lines.Count);
        JsonObject summary = ParseObject(lines[0], "play stdout");
        Equal("run/1", Text(summary, "format"));
        Equal("turn_limit", Text(summary, "kind"));
        Equal(TurnLimitTicks, Number(summary, "tick"));
        Equal("human", Text(summary, "agent_name"));
        Require(summary["error"] is null, "A redirected human episode reported an error: " + summary["error"]?.ToJsonString());
        Require(File.Exists(record), "play --record created no recording.");

        string verifyStdout = Path.Combine(_directory, "play-verify-stdout.txt");
        RunCliResult verify = await RunCliAsync(["verify", record], standardOutputPath: verifyStdout);
        Require(verify.ExitCode == 0, "verify exited " + verify.ExitCode + ": " + verify.Stderr.Trim());
        List<string> verifiedLines = ParseLines(ReadRedirectedText(verifyStdout));
        Equal(1, verifiedLines.Count);
        JsonObject verified = ParseObject(verifiedLines[0], "play verify stdout");
        Require(verified["valid"]?.GetValue<bool>() == true, "The human recording failed verification: " + verified.ToJsonString());
        Equal("completed", Text(verified, "status"));
        Equal(TurnLimitTicks, Number(verified, "tick"));
        Equal(Text(summary, "core_hash"), Text(verified, "core_hash"));
    }

    // 4) --scenario is mandatory and unknown flags are refused: usage errors never write stdout.
    private static async Task PlayUsageAsync()
    {
        RequireCli();
        string missingStdout = Path.Combine(_directory, "play-missing-stdout.txt");
        RunCliResult missing = await RunCliAsync(["play"], standardOutputPath: missingStdout);
        Equal(2, missing.ExitCode);
        Require(ReadRedirectedBytes(missingStdout).Length == 0, "play without --scenario wrote stdout.");
        Require(missing.Stderr.Contains("--scenario", StringComparison.Ordinal),
            "play without --scenario did not name the missing option: '" + missing.Stderr.Trim() + "'");

        string unknownStdout = Path.Combine(_directory, "play-unknown-stdout.txt");
        RunCliResult unknown = await RunCliAsync(
            ["play", "--scenario", _fixture, "--unknown"], standardOutputPath: unknownStdout);
        Equal(2, unknown.ExitCode);
        Require(ReadRedirectedBytes(unknownStdout).Length == 0, "play with an unknown option wrote stdout.");
        Require(unknown.Stderr.Contains("--unknown", StringComparison.Ordinal),
            "play did not name the rejected option on stderr: '" + unknown.Stderr.Trim() + "'");
    }

    // 5) The plain human UI is text only (no ANSI) and still teaches the controls.
    private static async Task PlayNoAnsiAsync()
    {
        RequireCli();
        string scenario = TurnLimitScenario();
        string record = Path.Combine(_directory, "play-plain.jsonl");
        string stdout = Path.Combine(_directory, "play-plain-stdout.txt");
        RunCliResult play = await RunCliAsync(
            ["play", "--scenario", scenario, "--plain", "--record", record],
            standardInputPath: EmptyInput(), standardOutputPath: stdout);
        Require(play.ExitCode == 0, "play --plain exited " + play.ExitCode + ": " + play.Stderr.Trim());
        string captured = ReadRedirectedText(stdout);
        Require(!HasEscape(captured), "play --plain wrote ANSI escapes to stdout.");
        Require(!HasEscape(play.Stderr), "play --plain wrote ANSI escapes to stderr.");
        Require(play.Stderr.Contains("pickup", StringComparison.Ordinal)
            || play.Stderr.Contains("wait", StringComparison.Ordinal),
            "play --plain showed no key legend on stderr: '" + play.Stderr.Trim() + "'");
        Require(play.Stderr.Contains(Legend, StringComparison.Ordinal),
            "play --plain did not show the full key legend on stderr: '" + play.Stderr.Trim() + "'");
    }

    /// <summary>The manual corridor with a 13-turn budget, written once per run. Located outside the
    /// fixture directory so the shared fixture is never modified; written without a BOM and without
    /// rewriting the bytes of untouched properties.</summary>
    private static string TurnLimitScenario()
    {
        if (_turnLimitScenario is null)
        {
            JsonNode scene = JsonNode.Parse(File.ReadAllText(_fixture, new UTF8Encoding(false, true)))!;
            scene["max_ticks"] = (int)TurnLimitTicks;
            _turnLimitScenario = Path.Combine(_directory, "turn-limit-scenario.json");
            File.WriteAllText(_turnLimitScenario, scene.ToJsonString(), new UTF8Encoding(false, true));
        }
        return _turnLimitScenario;
    }

    /// <summary>An empty stdin file: EOF from the very first byte, so the human prompt can never
    /// block and the episode has to be driven by the CLI's own fallback.</summary>
    private static string EmptyInput()
    {
        string path = Path.Combine(_directory, "empty-stdin.txt");
        if (!File.Exists(path)) File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>
    /// Runs the CLI with an exact argument list, an explicit deadline and a full process-tree kill
    /// on expiry. Both output pipes are always redirected and drained, so the child never sees a
    /// console; <paramref name="standardOutputPath"/> additionally publishes the captured stdout
    /// bytes to a file for byte-level inspection, and <paramref name="standardInputPath"/> feeds
    /// stdin from a file (an empty file is EOF from the first byte). A timed-out child is a failure,
    /// never a pass.
    /// </summary>
    private static async Task<RunCliResult> RunCliAsync(string[] arguments,
        string? standardInputPath = null, string? standardOutputPath = null,
        Dictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root, UseShellExecute = false,
            RedirectStandardInput = standardInputPath is not null,
            // Both pipes are always redirected, so the child never sees a console on stdout or
            // stderr even though the parent later publishes a file copy of what it captured.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(_cli);
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutBytes = new List<byte>();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            Require(process.Start() is true, "Failed to start the CLI process.");
            if (standardInputPath is not null)
            {
                using var stdin = process.StandardInput.BaseStream;
                byte[] input = File.ReadAllBytes(standardInputPath);
                await stdin.WriteAsync(input);
                await stdin.FlushAsync();
            }
            Task captureStdout = PumpAsync(process.StandardOutput.BaseStream, stdout, stdoutBytes);
            Task captureStderr = PumpAsync(process.StandardError.BaseStream, stderr, null);

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
                // The pipes reach EOF when the child exits; a leaked inherited handle must not hang
                // the check, so the drains are awaited with a bound instead of forever.
                try { await Task.WhenAll(captureStdout, captureStderr).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }
            string captured, diagnostics;
            byte[] bytes;
            lock (stdout) captured = stdout.ToString();
            lock (stderr) diagnostics = stderr.ToString();
            lock (stdoutBytes) bytes = [.. stdoutBytes];
            // The file is written after the child is gone, so the check has both the exact captured
            // bytes and a redirected-stdout artifact to inspect.
            if (standardOutputPath is not null && !timedOut) await File.WriteAllBytesAsync(standardOutputPath, bytes);
            if (timedOut)
                throw new Exception("The CLI did not finish within " + CliTimeout.TotalSeconds
                    + " seconds and its process tree was killed: " + diagnostics.Trim());
            Require(process.HasExited, "CLI process did not exit.");
            return new(process.ExitCode, captured, diagnostics, false);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    /// <summary>Drains one child pipe into <paramref name="sink"/>, and into
    /// <paramref name="bytes"/> when byte-level evidence is wanted. Reading is best-effort: a pipe
    /// that never reaches EOF stops feeding the caller instead of hanging it.</summary>
    private static async Task PumpAsync(Stream source, StringBuilder sink, List<byte>? bytes)
    {
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read;
            try { read = await source.ReadAsync(buffer); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { return; }
            if (read <= 0) return;
            string text = Encoding.UTF8.GetString(buffer, 0, read);
            lock (sink) sink.Append(text);
            if (bytes is not null) lock (bytes) bytes.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }

    private static void RequireCli() =>
        Require(File.Exists(_cli), "CLI assembly not found at " + _cli + ".");

    private static string ReadRedirectedText(string path) =>
        File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false, true)) : "";

    private static byte[] ReadRedirectedBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : [];

    /// <summary>True when the text carries the ESC control byte (0x1B): the start of every ANSI
    /// escape sequence a terminal renderer would emit.</summary>
    private static bool HasEscape(string text) => text.Contains('\u001b');

    private static List<string> ParseLines(string text) =>
        [.. text.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0)];

    private static JsonObject? TryParseObject(string line)
    {
        if (line.Trim().Length == 0) return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static JsonObject ParseObject(string line, string context)
    {
        try { return JsonNode.Parse(line) as JsonObject ?? throw new Exception(context + " is not a JSON object."); }
        catch (System.Text.Json.JsonException error) { throw new Exception(context + " is not valid JSON: " + error.Message); }
    }

    private static string Text(JsonObject node, string field) =>
        node[field]?.GetValue<string>() ?? throw new Exception("Missing string field '" + field + "': " + node.ToJsonString());

    private static long Number(JsonObject node, string field) =>
        node[field]?.GetValue<long>() ?? throw new Exception("Missing number field '" + field + "': " + node.ToJsonString());

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed record RunCliResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
}
