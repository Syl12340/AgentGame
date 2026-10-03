using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Black-box checks for the replay command surface added in M5: the plain export must keep
/// working after the flag parsing was split out, and every interactive or paced misuse must be
/// rejected as a usage error without writing anything to stdout.
/// </summary>
internal static class M6ReplayCliChecks
{
    private static string _root = "";
    private static string _cli = "";
    private static string _directory = "";

    public static async Task<int> RunAsync(string root)
    {
        _root = root;
        _cli = Path.Combine(root, "src", "AgentGame.Cli", "bin", "Release", "net10.0", "agent-game.dll");
        _directory = Path.Combine(root, "artifacts", "m6-replay-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Recording one human episode then exporting it still round-trips through replay", ExportAsync),
            ("replay --tui is rejected when stdout is redirected", TuiRejectedAsync),
            ("replay --speed requires --tui and validates its range", SpeedAsync),
            ("replay rejects unknown options and extra record files", UsageAsync)
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M6 Replay CLI: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    // The record is produced without Python: a redirected human episode answers wait and hits
    // the turn limit, which is enough to obtain a real recording to replay.
    private static async Task<string> RecordAsync(string name)
    {
        string scenario = Path.Combine(_directory, name + "-scene.json");
        File.WriteAllText(scenario, FixtureScenarioWithTurnLimit(), new UTF8Encoding(false));
        string record = Path.Combine(_directory, name + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        RunCliResult recorded = await RunCliAsync(["play", "--scenario", scenario, "--plain", "--record", record], emptyStdin: true);
        Require(recorded.ExitCode == 0, "play exited " + recorded.ExitCode + ": " + recorded.Stderr.Trim());
        Require(File.Exists(record), "play recorded no file.");
        return record;
    }

    private static async Task ExportAsync()
    {
        string record = await RecordAsync("export");
        RunCliResult export = await RunCliAsync(["replay", record]);
        Require(export.ExitCode == 0, "replay exited " + export.ExitCode + ": " + export.Stderr.Trim());
        List<string> lines = ParseLines(export.Stdout);
        Require(lines.Count >= 2, "replay exported only " + lines.Count + " lines.");
        JsonObject first = ParseObject(lines[0], "first envelope");
        Equal("snapshot", first["type"]!.GetValue<string>());
        JsonObject summary = ParseObject(ParseLines(export.Stderr).Single(), "replay summary");
        Equal("completed", summary["status"]!.GetValue<string>());
    }

    private static async Task TuiRejectedAsync()
    {
        string record = await RecordAsync("tui");
        RunCliResult rejected = await RunCliAsync(["replay", record, "--tui"]);
        Equal(2, rejected.ExitCode);
        Require(rejected.Stdout.Trim().Length == 0, "rejected --tui still wrote stdout: " + rejected.Stdout.Trim());
        Require(rejected.Stderr.Contains("interactive terminal", StringComparison.Ordinal),
            "rejection did not explain the terminal requirement: '" + rejected.Stderr.Trim() + "'");
    }

    private static async Task SpeedAsync()
    {
        string record = await RecordAsync("speed");
        RunCliResult withoutTui = await RunCliAsync(["replay", record, "--speed", "2"]);
        Equal(2, withoutTui.ExitCode);
        Require(withoutTui.Stderr.Contains("--speed requires --tui", StringComparison.Ordinal),
            "missing --tui was not explained: '" + withoutTui.Stderr.Trim() + "'");
        foreach (string speed in new[] { "0.1", "17", "fast" })
        {
            RunCliResult range = await RunCliAsync(["replay", record, "--tui", "--speed", speed]);
            Equal(2, range.ExitCode);
            Require(range.Stdout.Trim().Length == 0, "invalid --speed " + speed + " wrote stdout.");
        }
    }

    private static async Task UsageAsync()
    {
        string record = await RecordAsync("usage");
        RunCliResult unknown = await RunCliAsync(["replay", record, "--wat"]);
        Equal(2, unknown.ExitCode);
        RunCliResult extra = await RunCliAsync(["replay", record, record]);
        Equal(2, extra.ExitCode);
        RunCliResult missing = await RunCliAsync(["replay", "--tui"]);
        Equal(2, missing.ExitCode);
    }

    private static string FixtureScenarioWithTurnLimit()
    {
        JsonNode scene = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "tests", "Fixtures", "Core", "facility-small.json"),
            new UTF8Encoding(false, true)))!;
        scene["max_ticks"] = 13;
        return scene.ToJsonString();
    }

    private sealed record RunCliResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<RunCliResult> RunCliAsync(string[] arguments, bool emptyStdin = false, bool captureStdout = true)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = captureStdout, RedirectStandardError = true, RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add(_cli);
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            Require(process.Start(), "Failed to start the CLI.");
            process.StandardInput.Close();
            Task<string> stdout = captureStdout ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                throw new Exception("CLI timed out.");
            }
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    private static List<string> ParseLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];

    private static JsonObject ParseObject(string line, string context)
    {
        try { return JsonNode.Parse(line)!.AsObject(); }
        catch (Exception error) { throw new Exception("Malformed " + context + " JSON: " + error.Message); }
    }

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
