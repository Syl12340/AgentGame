using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Black-box golden-vector contract checks for M6. Every check launches the real
/// <c>dotnet &lt;cli dll&gt;</c> child process through <see cref="ProcessStartInfo.ArgumentList"/>
/// with the repository root as its working directory, gives it an explicit deadline and kills its
/// whole tree on expiry. The assertions are intentionally strict: cross-process determinism,
/// agreement with the independent Python vectors frozen in <c>core-golden.json</c>, read-only
/// byte-exact verification, and observer-mode invariance. Nothing here calls dotnet test, reads
/// credentials, uses the network, changes global configuration, or writes outside the per-run
/// temporary directory below <c>artifacts/m6-golden</c>.
/// </summary>
internal static class M6GoldenChecks
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(3);

    private static string _root = "";
    private static string _cli = "";
    private static string _directory = "";
    private static string _python = "";
    private static string _scenario = "";
    private static string _agent = "";

    /// <summary>The independent Python success hash frozen in tests/Fixtures/Core/core-golden.json.</summary>
    private const string FrozenSuccessHash = "a68fe87640e7105142f481a59e77f43bdb854bab61a826e38ae4fd77ae323262";

    /// <summary>The independent Python initial hash frozen in tests/Fixtures/Core/core-golden.json.</summary>
    private const string FrozenInitialHash = "932e1d591f8e19764177838757f3117c8dbcd19e202e47f806ff4bb50c49d286";

    public static async Task<int> RunAsync(string root)
    {
        _root = root;
        _cli = Path.Combine(root, "src", "AgentGame.Cli", "bin", "Release", "net10.0", "agent-game.dll");
        _directory = Path.Combine(root, "artifacts", "m6-golden", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        _scenario = Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json");
        _agent = Path.Combine(root, "tests", "Fixtures", "Agents", "fault_agent.py");

        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Two separate CLI runs reproduce identical success hashes and the frozen vector",
                CrossProcessDeterminismAsync),
            ("Frozen vectors still agree through the CLI validate and verify surfaces",
                FrozenVectorsThroughCliAsync),
            ("Verification is deterministic, read-only and byte-stable on the record file",
                VerifyReadOnlyAsync),
            ("Attaching an observer-stdout cannot change the rule result",
                ObserverInvarianceAsync)
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M6 Golden: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    /// <summary>Runs the same fixed fixed-action episode twice into two different record files and
    /// requires both summaries to be exactly one JSON line, kind=success, tick=13 and byte-identical
    /// core_hash, and that hash to equal the frozen golden success hash.</summary>
    private static async Task CrossProcessDeterminismAsync()
    {
        RequireCli();
        string[] records = new string[2];
        JsonObject[] summaries = new JsonObject[2];
        for (int index = 0; index < 2; index++)
        {
            records[index] = Path.Combine(_directory, "determinism-" + index + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            RunCliResult run = await RunCliAsync(
                ["run", "--scenario", _scenario, "--headless", "--record", records[index],
                    "--", _python, "-u", _agent, "--mode", "success"], captureStdout: true);
            Require(run.ExitCode == 0, "run #" + index + " exited " + run.ExitCode + ": " + run.Stderr.Trim());
            summaries[index] = OnlyJsonLine(run, "run #" + index);
            Equal("run/1", Text(summaries[index], "format"));
            Equal("success", Text(summaries[index], "kind"));
            Equal(13L, Num(summaries[index], "tick"));
            Require(summaries[index]["error"] is null, "run #" + index + " reported an error: " + summaries[index]["error"]?.ToJsonString());
        }
        Equal(Text(summaries[0], "core_hash"), Text(summaries[1], "core_hash"));
        Equal(FrozenSuccessHash, Text(summaries[0], "core_hash"));
    }

    /// <summary>scenario validate must report the frozen initial_hash, the fixed success run must
    /// reproduce the frozen success hash, and CLI verify on those records must report valid=true with
    /// the same core_hash.</summary>
    private static async Task FrozenVectorsThroughCliAsync()
    {
        RequireCli();
        RunCliResult validate = await RunCliAsync(["scenario", "validate", _scenario], captureStdout: true);
        Require(validate.ExitCode == 0, "scenario validate exited " + validate.ExitCode + ": " + validate.Stderr.Trim());
        JsonObject validated = OnlyJsonLine(validate, "scenario validate");
        Require(validated["valid"]?.GetValue<bool>() == true, "Scenario validate did not accept the fixture: " + validated.ToJsonString());
        Equal(FrozenInitialHash, Text(validated, "initial_hash"));

        string record = Path.Combine(_directory, "frozen-run-" + Guid.NewGuid().ToString("N") + ".jsonl");
        RunCliResult run = await RunCliAsync(
            ["run", "--scenario", _scenario, "--headless", "--record", record,
                "--", _python, "-u", _agent, "--mode", "success"], captureStdout: true);
        Require(run.ExitCode == 0, "run exited " + run.ExitCode + ": " + run.Stderr.Trim());
        JsonObject summary = OnlyJsonLine(run, "frozen run");
        Equal(FrozenSuccessHash, Text(summary, "core_hash"));

        RunCliResult verify = await RunCliAsync(["verify", record], captureStdout: true);
        Require(verify.ExitCode == 0, "verify exited " + verify.ExitCode + ": " + verify.Stderr.Trim());
        JsonObject verified = OnlyJsonLine(verify, "verify");
        Require(verified["valid"]?.GetValue<bool>() == true, "Frozen run failed verification: " + verified.ToJsonString());
        Equal(Text(summary, "core_hash"), Text(verified, "core_hash"));
    }

    /// <summary>Runs verify twice on the same record and requires byte-identical stdout each time,
    /// and that the record file's bytes (and its length) are unchanged by either verify run.</summary>
    private static async Task VerifyReadOnlyAsync()
    {
        RequireCli();
        string record = Path.Combine(_directory, "read-only-" + Guid.NewGuid().ToString("N") + ".jsonl");
        RunCliResult run = await RunCliAsync(
            ["run", "--scenario", _scenario, "--headless", "--record", record,
                "--", _python, "-u", _agent, "--mode", "success"], captureStdout: true);
        Require(run.ExitCode == 0, "run exited " + run.ExitCode + ": " + run.Stderr.Trim());

        byte[] before = File.ReadAllBytes(record);
        long beforeLength = new FileInfo(record).Length;

        byte[] first = (await RunCliAsync(["verify", record], captureStdout: true)).StdoutBytes;
        byte[] afterFirst = File.ReadAllBytes(record);
        byte[] second = (await RunCliAsync(["verify", record], captureStdout: true)).StdoutBytes;
        byte[] afterSecond = File.ReadAllBytes(record);

        Require(first.Length > 0, "verify produced empty stdout on the first run.");
        Exact(first, second);
        // verify must be a pure projection: neither run may touch the record's contents or length.
        Exact(before, afterFirst);
        Exact(before, afterSecond);
        Equal(beforeLength, afterSecond.Length);
    }

    /// <summary>Runs the same fixed episode headless and with --observer-stdout (whose streamed stdout
    /// is discarded, the summary being read from stderr) and requires the two summaries to carry the
    /// same tick and core_hash: attaching an observer cannot change rule results.</summary>
    private static async Task ObserverInvarianceAsync()
    {
        RequireCli();
        string headlessRecord = Path.Combine(_directory, "observer-headless-" + Guid.NewGuid().ToString("N") + ".jsonl");
        RunCliResult headless = await RunCliAsync(
            ["run", "--scenario", _scenario, "--headless", "--record", headlessRecord,
                "--", _python, "-u", _agent, "--mode", "success"], captureStdout: true);
        Require(headless.ExitCode == 0, "headless run exited " + headless.ExitCode + ": " + headless.Stderr.Trim());
        JsonObject headlessSummary = OnlyJsonLine(headless, "headless run");

        string observerRecord = Path.Combine(_directory, "observer-stream-" + Guid.NewGuid().ToString("N") + ".jsonl");
        // The observer stream is deliberately discarded; the run/1 summary lives on stderr.
        RunCliResult observer = await RunCliAsync(
            ["run", "--scenario", _scenario, "--observer-stdout", "--record", observerRecord,
                "--", _python, "-u", _agent, "--mode", "success"], captureStdout: true);
        Require(observer.ExitCode == 0, "observer-stdout run exited " + observer.ExitCode + ": " + observer.Stderr.Trim());
        List<string> stderrLines = ParseLines(observer.Stderr);
        string? summaryLine = stderrLines
            .Select(line => TryParseObject(line))
            .OfType<JsonObject>()
            .FirstOrDefault(node => node["format"]?.GetValue<string>() == "run/1") is { } summary
                ? summary.ToJsonString()
                : null;
        Require(summaryLine is not null, "observer-stdout run wrote no run/1 summary to stderr: '" + observer.Stderr.Trim() + "'");
        JsonObject observerSummary = ParseObject(summaryLine!, "observer run summary");

        Equal(Text(headlessSummary, "kind"), Text(observerSummary, "kind"));
        Equal(Num(headlessSummary, "tick"), Num(observerSummary, "tick"));
        Equal(Text(headlessSummary, "core_hash"), Text(observerSummary, "core_hash"));
        // Attaching an observer must neither truncate nor corrupt the committed prefix length.
        Require(File.Exists(observerRecord), "observer-stdout run recorded no file.");
    }

    private static void RequireCli() =>
        Require(File.Exists(_cli), "CLI assembly not found at " + _cli + ".");

    /// <summary>
    /// Runs the CLI with an exact argument list, an explicit deadline and a full process-tree kill on
    /// expiry. Both output pipes are always redirected and drained, so the child never sees a console;
    /// when <paramref name="captureStdout"/> is true the exact stdout bytes are also retained. A
    /// timed-out child is a failure, never a pass.
    /// </summary>
    private static async Task<RunCliResult> RunCliAsync(string[] arguments, bool captureStdout,
        Dictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
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
            Task captureOut = PumpAsync(process.StandardOutput.BaseStream, stdout, stdoutBytes);
            Task captureErr = PumpAsync(process.StandardError.BaseStream, stderr, null);

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
                try { await Task.WhenAll(captureOut, captureErr).WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }
            string captured, diagnostics;
            byte[] bytes;
            lock (stdout) captured = stdout.ToString();
            lock (stderr) diagnostics = stderr.ToString();
            lock (stdoutBytes) bytes = [.. stdoutBytes];
            if (timedOut)
                throw new Exception("The CLI did not finish within " + CliTimeout.TotalSeconds
                    + " seconds and its process tree was killed: " + diagnostics.Trim());
            Require(process.HasExited, "CLI process did not exit.");
            return new(process.ExitCode, captured, diagnostics, bytes, false);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    /// <summary>Drains one child pipe into <paramref name="sink"/>, and into <paramref name="bytes"/>
    /// when byte-level evidence is wanted. Reading is best-effort so a never-EOF pipe cannot hang the
    /// check.</summary>
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

    private static JsonObject OnlyJsonLine(RunCliResult result, string context)
    {
        List<string> lines = ParseLines(result.Stdout);
        Equal(1, lines.Count);
        return ParseObject(lines[0], context + " stdout");
    }

    private static List<string> ParseLines(string text) =>
        [.. text.Replace("\r\n", "\n").Split('\n').Where(line => line.Trim().Length > 0)];

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

    private static long Num(JsonObject node, string field) =>
        node[field]?.GetValue<long>() ?? throw new Exception("Missing number field '" + field + "': " + node.ToJsonString());

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }

    private static void Exact(byte[] expected, byte[] actual)
    {
        Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
            if (expected[index] != actual[index])
                throw new Exception($"Byte {index} differs: expected 0x{expected[index]:x2}, got 0x{actual[index]:x2}.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed record RunCliResult(int ExitCode, string Stdout, string Stderr, byte[] StdoutBytes, bool TimedOut);
}