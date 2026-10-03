using System.Diagnostics;
using System.Text;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Agents;

internal static class M3Checks
{
    public static async Task<int> RunAsync(string root)
    {
        ScenarioDto scene = ScenarioService.Read(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json"));
        string fixture = Path.Combine(root, "tests", "Fixtures", "Agents", "fault_agent.py");
        string python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        var fast = new AgentTimeouts
        {
            Handshake = TimeSpan.FromSeconds(2), Decision = TimeSpan.FromSeconds(2),
            ShutdownGrace = TimeSpan.FromMilliseconds(150), KillWait = TimeSpan.FromSeconds(1)
        };
        AgentCommand Command(string mode, params string[] extra) => new(python, new[] { "-u", fixture, "--mode", mode }.Concat(extra).ToArray(), root);
        async Task<RunResult> Run(string mode, int ticks = 13, AgentTimeouts? limits = null)
        {
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await GameRunner.RunAsync(scene with { MaxTicks = ticks }, Command(mode), limits ?? fast, watchdog.Token)
                .WaitAsync(TimeSpan.FromSeconds(12));
        }
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("Fragmented CRLF preserves content and following line", async () =>
            {
                using var input = new ChunkedInput(Encoding.UTF8.GetBytes("abc\r\nxyz\n"), 1);
                var transport = new JsonLineTransport(input, Stream.Null, 3);
                Equal("abc", Encoding.UTF8.GetString(await transport.ReadLineAsync(default)));
                Equal("xyz", Encoding.UTF8.GetString(await transport.ReadLineAsync(default)));
                await Throws<EndOfStreamException>(() => transport.ReadLineAsync(default));
            }),
            ("Buffered duplicate bytes remain visible to the owner", async () =>
            {
                var transport = new JsonLineTransport(new MemoryStream("a\nb\n"u8.ToArray()), Stream.Null);
                Equal("a", Encoding.UTF8.GetString(await transport.ReadLineAsync(default)));
                Require(transport.HasBufferedData && await transport.ReadTrailingByteAsync(default), "Extra bytes lost.");
            }),
            ("Line overflow is rejected before newline or EOF", async () =>
            {
                var transport = new JsonLineTransport(new MemoryStream("abcd"u8.ToArray()), Stream.Null, 3);
                var error = await Throws<JsonLineException>(() => transport.ReadLineAsync(default)); Equal("line_too_long", error.Code);
            }),
            ("Unterminated final line is not a valid JSONL response", async () =>
            {
                var transport = new JsonLineTransport(new MemoryStream("abc"u8.ToArray()), Stream.Null, 3);
                Equal("incomplete_line", (await Throws<JsonLineException>(() => transport.ReadLineAsync(default))).Code);
            }),
            ("UTF8 byte count rather than character count enforces limit", async () =>
            {
                var transport = new JsonLineTransport(new MemoryStream(Encoding.UTF8.GetBytes("中文\n")), Stream.Null, 5);
                Equal("line_too_long", (await Throws<JsonLineException>(() => transport.ReadLineAsync(default))).Code);
            }),
            ("Writes append LF and flush without raw newlines or over-limit bytes", async () =>
            {
                using var output = new MemoryStream();
                var transport = new JsonLineTransport(Stream.Null, output, 3);
                await transport.WriteLineAsync("abc"u8.ToArray(), default); Equal("abc\n", Encoding.UTF8.GetString(output.ToArray()));
                Equal("host_message_too_large", (await Throws<JsonLineException>(() => transport.WriteLineAsync("abcd"u8.ToArray(), default))).Code);
                await Throws<JsonLineException>(() => transport.WriteLineAsync("a\nb"u8.ToArray(), default));
            }),
            ("Blocked send obeys caller deadline", async () =>
            {
                var transport = new JsonLineTransport(Stream.Null, new BlockedOutput());
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Throws<OperationCanceledException>(() => transport.WriteLineAsync("{}"u8.ToArray(), deadline.Token));
            }),
            ("External success route matches the independent Core golden hash", async () =>
            {
                RunResult result = await GameRunner.RunAsync(scene, Command("success"), fast);
                Equal("success", result.Kind); Equal(13L, result.Tick); Require(result.Terminated && !result.Truncated, "Wrong termination flags.");
                Equal("r12", result.LastRequestId); Equal(ActionTypeDto.Move, result.LastAction!.Type);
                Equal("a68fe87640e7105142f481a59e77f43bdb854bab61a826e38ae4fd77ae323262", result.CoreHash);
                Equal(0, result.AgentExitCode); Require(result.Error is null && !result.ForcedTermination, "Normal exit failed."); Gone(result);
            }),
            ("Wait Agent ends by turn limit with the last accepted action", async () =>
            {
                var result = await Run("wait"); Equal("turn_limit", result.Kind); Equal(13L, result.Tick);
                Require(!result.Terminated && result.Truncated && result.Error is null, "Wrong truncation result.");
                Equal("r12", result.LastRequestId); Equal(ActionTypeDto.Wait, result.LastAction!.Type); Gone(result);
            }),
            ("Last permitted turn succeeds over truncation", async () =>
            {
                var result = await Run("success", 13); Equal("success", result.Kind); Equal(13L, result.Tick); Gone(result);
            }),
            ("Random Agent uses only the protocol and reproducibly reaches its budget", async () =>
            {
                var command = new AgentCommand(python, ["-u", Path.Combine(root, "agents", "random_agent.py"), "--seed", "42"], root);
                var first = await GameRunner.RunAsync(scene with { MaxTicks = 13 }, command, fast);
                var second = await GameRunner.RunAsync(scene with { MaxTicks = 13 }, command, fast);
                Equal("turn_limit", first.Kind); Equal(13L, first.Tick); Equal(first.CoreHash, second.CoreHash);
                Equal(first.LastAction, second.LastAction); Equal("random", first.AgentName); Gone(first); Gone(second);
            }),
            ("Agent input contains only current local observation", async () =>
            { var result = await Run("privacy"); Equal("turn_limit", result.Kind); Require(result.Error is null, "Information boundary failed."); Gone(result); }),
            ("ArgumentList preserves Unicode, spaces and shell metacharacters", async () =>
            {
                string marker = "中文 spaces ; & $() ` quoted \"value\"";
                var result = await GameRunner.RunAsync(scene with { MaxTicks = 13 }, Command("argv", "--marker", marker), fast);
                Equal(marker, result.AgentName); Equal("turn_limit", result.Kind); Gone(result);
            }),
            ("Failure after a valid action preserves the accepted boundary", async () =>
            {
                var result = await Run("valid-then-wrong"); Equal("execution_error", result.Kind); Equal(1L, result.Tick);
                Equal("r0", result.LastRequestId); Equal(ActionTypeDto.Wait, result.LastAction!.Type);
                Equal("protocol_violation", result.Error!.Code); Equal("r1", result.Error.RequestId); Gone(result);
            }),
            ("Stderr flood is drained and bounded without affecting turns", async () =>
            {
                var result = await Run("stderr-flood", 13); Equal("turn_limit", result.Kind); Equal(13L, result.Tick);
                Require(result.StderrTail.TrimEnd('\r', '\n').EndsWith("TAIL_MARKER", StringComparison.Ordinal), "Missing stderr tail.");
                Require(Encoding.UTF8.GetByteCount(result.StderrTail) <= 65536, "Unbounded stderr."); Gone(result);
            }),
            ("Uncooperative shutdown is forcibly terminated within budget", async () =>
            {
                var timer = Stopwatch.StartNew(); var result = await Run("ignore-shutdown", 13);
                Equal("turn_limit", result.Kind); Require(result.ForcedTermination && timer.Elapsed < TimeSpan.FromSeconds(5), "Cleanup not bounded."); Gone(result);
            }),
            ("Cancellation before startup creates no Agent process", async () =>
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                var result = await GameRunner.RunAsync(scene, Command("wait"), fast, cancellation.Token);
                Equal("cancelled", result.Kind); Equal(0L, result.Tick); Require(result.AgentProcessId is null, "Cancelled run spawned an Agent.");
            }),
            ("Cancellation during a pending decision never applies an action", async () =>
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                var result = await GameRunner.RunAsync(scene, Command("silent"), fast, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
                Equal("cancelled", result.Kind); Equal(0L, result.Tick); Require(result.LastAction is null, "Cancellation applied an action."); Gone(result);
            }),
            ("Startup failure returns structured error with initial state", async () =>
            {
                var result = await GameRunner.RunAsync(scene, new(Path.Combine(root, "missing-agent-executable"), []), fast);
                Equal("execution_error", result.Kind); Equal("start_failed", result.Error!.Code); Equal(0L, result.Tick);
                Equal(ScenarioService.Validate(scene).InitialHash, result.CoreHash); Require(result.AgentProcessId is null, "Unexpected process.");
            }),
            ("Unexpected stdout after terminal action is an execution error", async () =>
            {
                var result = await Run("terminal-output"); Equal("execution_error", result.Kind);
                Equal("protocol_violation", result.Error!.Code); Equal(13L, result.Tick); Require(result.Truncated, "Lost completed rule outcome."); Gone(result);
            }),
            ("Nonzero terminal exit preserves rule outcome and reports Agent failure", async () =>
            {
                var result = await Run("terminal-exit"); Equal("execution_error", result.Kind);
                Equal("agent_exited", result.Error!.Code); Equal(7, result.AgentExitCode); Require(result.Truncated, "Lost truncation."); Gone(result);
            }),
            ("Oversized outgoing observation fails before the first rule action", async () =>
            {
                string[] rows = Enumerable.Range(0, 32).Select(y => new string(Enumerable.Range(0, 128)
                    .Select(x => y is 0 or 31 || x is 0 or 127 || (x == 64 && y != 16) ? '#' : '.').ToArray())).ToArray();
                var large = scene with
                {
                    Rows = rows, Start = new() { X = 2, Y = 16 }, Exit = new() { X = 2, Y = 16 },
                    Key = new() { X = 3, Y = 16 }, Door = new() { X = 64, Y = 16 }, Core = new() { X = 65, Y = 16 }, VisibilityRadius = 128
                };
                var result = await GameRunner.RunAsync(large, Command("wait"), fast).WaitAsync(TimeSpan.FromSeconds(10));
                Equal("execution_error", result.Kind); Equal("host_message_too_large", result.Error!.Code); Equal(0L, result.Tick); Gone(result);
            }),
            ("Invalid task is rejected before invoking a process", async () =>
            {
                var invalid = ScenarioService.Read(Path.Combine(root, "tests", "Fixtures", "Core", "invalid-door-bypass.json"));
                await Throws<ProtocolException>(() => GameRunner.RunAsync(invalid, Command("wait"), fast));
            })
        };
        foreach (var (mode, code, phase) in new[]
        {
            ("wrong-id", "protocol_violation", "decision"), ("duplicate", "protocol_violation", "decision"),
            ("invalid-action", "protocol_violation", "decision"), ("invalid-utf8", "protocol_violation", "decision"),
            ("blank", "protocol_violation", "decision"), ("wrong-ready", "protocol_violation", "handshake"),
            ("oversized", "line_too_long", "decision"), ("no-newline-eof", "incomplete_line", "decision"),
            ("partial", "decision_timeout", "decision"), ("silent", "decision_timeout", "decision"),
            ("no-read", "handshake_timeout", "handshake"), ("exit-before-ready", "agent_exited", "handshake"),
            ("exit-after-ready", "agent_exited", "decision")
        })
        {
            checks.Add(($"Fault {mode} rejects input without advancing Core", async () =>
            {
                var limits = fast with { Decision = TimeSpan.FromMilliseconds(250) };
                if (mode == "no-read") limits = limits with { Handshake = TimeSpan.FromMilliseconds(250) };
                var result = await Run(mode, 13, limits);
                Equal("execution_error", result.Kind); Equal(code, result.Error!.Code); Equal(phase, result.Error.Phase);
                Equal(0L, result.Tick); Require(result.LastAction is null && !result.Terminated && !result.Truncated, "Invalid response changed state.");
                Equal(ScenarioService.Validate(scene with { MaxTicks = 13 }).InitialHash, result.CoreHash); Gone(result);
            }));
        }
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M3 Runtime: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static void Gone(RunResult result)
    {
        if (result.AgentProcessId is null) throw new Exception("No Agent PID to verify cleanup.");
        try { using var process = Process.GetProcessById(result.AgentProcessId.Value); Require(process.HasExited, "Agent process remains alive."); }
        catch (ArgumentException) { }
    }

    private sealed class ChunkedInput(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }
    private sealed class BlockedOutput : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
