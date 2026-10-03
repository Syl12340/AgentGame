using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

internal static class M6ProcessTreeChecks
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(string root)
    {
        string cli = Path.Combine(root, "src", "AgentGame.Cli", "bin", "Release", "net10.0", "agent-game.dll");
        string directory = Path.Combine(root, "artifacts", "m6-process-tree", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        string fixture = Path.Combine(root, "tests", "Fixtures", "Agents", "descendant_agent.py");
        string originalScenario = Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json");

        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("决策超时后整个进程树消失", () => TimeoutAsync(root, cli, python, fixture, originalScenario, directory)),
            ("拒绝正常关闭时宿主强制清理进程树", () => IgnoreShutdownAsync(root, cli, python, fixture, originalScenario, directory)),
            ("夹具自身有效性", () => FixtureValidityAsync(python, fixture, directory))
        };

        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M6 Process tree: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static async Task TimeoutAsync(string root, string cli, string python, string fixture, string originalScenario, string directory)
    {
        string scenario = Path.Combine(directory, "timeout-scenario.json");
        File.Copy(originalScenario, scenario, true);
        string pidfile = Path.Combine(directory, "timeout-pid.json");

        string[] arguments = ["run", "--scenario", scenario, "--headless", "--decision-ms", "1500", "--", python, "-u", fixture, "--mode", "descendants", "--pidfile", pidfile];

        RunResult result = await RunCliAsync(root, cli, arguments);
        
        Require(result.ExitCode == 1, $"Exit code was {result.ExitCode}, expected 1. Stderr: {result.Stderr}");
        List<string> lines = ParseLines(result.Stdout);
        Require(lines.Count == 1, $"Expected exactly 1 line of stdout, got {lines.Count}.");
        JsonObject summary = ParseObject(lines[0], "run summary");
        
        Equal("execution_error", summary["kind"]?.GetValue<string>());
        Require(summary["error"] is not null, "Summary error is null.");
        Equal("decision_timeout", summary["error"]!["code"]?.GetValue<string>());
        Equal(0L, summary["tick"]?.GetValue<long>());

        Require(File.Exists(pidfile), "Pidfile does not exist.");
        JsonObject pids = ParseObject(File.ReadAllText(pidfile, new UTF8Encoding(false, true)), "pidfile");
        int parentPid = pids["parent"]!.GetValue<int>();
        int childPid = pids["child"]!.GetValue<int>();

        if (summary["agent_process_id"] is not null)
        {
            Equal(summary["agent_process_id"]!.GetValue<int>(), parentPid);
        }

        await AssertProcessVanishedAsync(parentPid);
        await AssertProcessVanishedAsync(childPid);
    }

    private static async Task IgnoreShutdownAsync(string root, string cli, string python, string fixture, string originalScenario, string directory)
    {
        string scenario = Path.Combine(directory, "ignore-scenario.json");
        JsonObject scene = ParseObject(File.ReadAllText(originalScenario, new UTF8Encoding(false, true)), "scenario");
        scene["max_ticks"] = 13;
        File.WriteAllText(scenario, scene.ToJsonString(), new UTF8Encoding(false, true));
        
        string pidfile = Path.Combine(directory, "ignore-pid.json");

        string[] arguments = ["run", "--scenario", scenario, "--headless", "--", python, "-u", fixture, "--mode", "descendants-ignore-shutdown", "--pidfile", pidfile];

        RunResult result = await RunCliAsync(root, cli, arguments);
        
        Require(result.ExitCode == 0, $"Exit code was {result.ExitCode}, expected 0. Stderr: {result.Stderr}");
        List<string> lines = ParseLines(result.Stdout);
        Require(lines.Count == 1, $"Expected exactly 1 line of stdout, got {lines.Count}.");
        JsonObject summary = ParseObject(lines[0], "run summary");
        
        Equal("turn_limit", summary["kind"]?.GetValue<string>());
        Equal(13L, summary["tick"]?.GetValue<long>());
        Require(summary["forced_termination"]?.GetValue<bool>() == true, "forced_termination is not true.");

        Require(File.Exists(pidfile), "Pidfile does not exist.");
        JsonObject pids = ParseObject(File.ReadAllText(pidfile, new UTF8Encoding(false, true)), "pidfile");
        int parentPid = pids["parent"]!.GetValue<int>();
        int childPid = pids["child"]!.GetValue<int>();

        await AssertProcessVanishedAsync(parentPid);
        await AssertProcessVanishedAsync(childPid);
    }

    private static async Task FixtureValidityAsync(string python, string fixture, string directory)
    {
        string pidfile = Path.Combine(directory, "validity-pid.json");
        
        var startInfo = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(fixture);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("descendants");
        startInfo.ArgumentList.Add("--pidfile");
        startInfo.ArgumentList.Add(pidfile);

        using var process = new Process { StartInfo = startInfo };
        Require(process.Start() is true, "Failed to start standalone fixture.");
        
        try
        {
            await Task.Delay(2000);
            Require(File.Exists(pidfile), "Pidfile was not created by standalone fixture.");
            JsonObject pids = ParseObject(File.ReadAllText(pidfile, new UTF8Encoding(false, true)), "pidfile");
            int parentPid = pids["parent"]!.GetValue<int>();
            int childPid = pids["child"]!.GetValue<int>();
            
            Require(parentPid == process.Id, "Pidfile parent PID does not match process ID.");

            bool childRunning = false;
            try
            {
                using var childProc = Process.GetProcessById(childPid);
                childRunning = !childProc.HasExited;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            
            Require(childRunning, "Grandchild process is not running after 2 seconds.");

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();

            await AssertProcessVanishedAsync(parentPid);
            await AssertProcessVanishedAsync(childPid);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    private static async Task AssertProcessVanishedAsync(int pid)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!deadline.IsCancellationRequested)
        {
            bool running = false;
            try
            {
                using var proc = Process.GetProcessById(pid);
                running = !proc.HasExited;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            
            if (!running) return;
            await Task.Delay(100);
        }
        throw new Exception($"Process {pid} did not vanish within 10 seconds.");
    }

    private static async Task<RunResult> RunCliAsync(string root, string cli, string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(cli);
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        
        Require(process.Start() is true, "Failed to start the CLI process.");
        
        process.OutputDataReceived += (_, line) => { if (line.Data is not null) lock (stdout) stdout.AppendLine(line.Data); };
        process.BeginOutputReadLine();
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (stderr) stderr.AppendLine(line.Data); };
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(ProcessTimeout);
        bool timedOut = false;
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }
        
        process.CancelOutputRead();
        process.CancelErrorRead();
        
        string captured, diagnostics;
        lock (stdout) captured = stdout.ToString();
        lock (stderr) diagnostics = stderr.ToString();
        
        if (timedOut) throw new Exception($"CLI process timed out after {ProcessTimeout.TotalSeconds} seconds.");
        
        return new RunResult(process.ExitCode, captured, diagnostics);
    }

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr);

    private static List<string> ParseLines(string text) =>
        [.. text.Replace("\r\n", "\n").Split('\n').Where(line => line.Length > 0)];

    private static JsonObject ParseObject(string line, string context)
    {
        try { return JsonNode.Parse(line) as JsonObject ?? throw new Exception(context + " is not a JSON object."); }
        catch (JsonException error) { throw new Exception(context + " is not valid JSON: " + error.Message); }
    }

    private static void Equal<T>(T? expected, T? actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
