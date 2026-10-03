using System.Globalization;
using System.Text.Json;
using AgentGame.Runtime;
using AgentGame.Runtime.Agents;
using AgentGame.Protocol;
using AgentGame.Cli;
using AgentGame.Runtime.Replay;

if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h" or "help"))
{
    Console.WriteLine("Agent Game / Facility Zero — v0.1-dev (M0–M5)");
    Console.WriteLine("Usage: agent-game --help | --version | scenario generate | scenario validate | run | play | replay | verify");
    Console.WriteLine("  scenario generate --seed <UInt64> --out <new-file> [--max-ticks 512] [--max-reference-length 128] [--max-attempts 16]");
    Console.WriteLine("  scenario validate <file>");
    Console.WriteLine("Generation never overwrites an existing file. Summaries are JSON; errors go to stderr.");
    Console.WriteLine("  run --scenario <file> [--headless] [--tui] [--record <new-jsonl>] [--observer-stdout] [--handshake-ms 5000] [--decision-ms 30000] -- <executable> [arguments...]");
    Console.WriteLine("  play --scenario <file> [--record <new-jsonl>] [--plain]   Play it yourself; keys: arrows/WASD move, e+dir or Shift+dir interact, space pickup, . wait, q quit.");
    Console.WriteLine("  replay <jsonl> [--tui] [--speed 1.0]   Export the recorded Observer stream as JSONL, or replay it in the terminal with pause/step/speed controls.");
    Console.WriteLine("  verify <jsonl>     Check committed actions, hashes and patches against the saved scenario.");
    Console.WriteLine("Run prints one JSON summary; --observer-stdout streams Observer JSONL and sends its summary to stderr.");
    Console.WriteLine("--headless attaches no terminal observer; --tui renders the live map and accepts space/./-/+/q. They are mutually exclusive, and --tui needs an interactive terminal.");
    Console.WriteLine("Exit codes: 0 success/rule end, 1 failed run or invalid scenario, 2 usage error, 130 cancelled. The cause is the JSON error.code.");
    return 0;
}
if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine("agent-game 0.1.0-dev");
    return 0;
}
if (args[0] == "scenario")
{
    try
    {
        if (args.Length == 3 && args[1] == "validate")
        {
            ScenarioSummary summary = ScenarioService.Validate(ScenarioService.Read(args[2]));
            WriteSummary(summary);
            return summary.Valid ? 0 : 1;
        }
        if (args.Length < 2 || args[1] != "generate") throw new ArgumentException("Expected scenario generate or scenario validate <file>.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 2; i < args.Length; i += 2)
        {
            if (args[i] is not ("--seed" or "--out" or "--max-ticks" or "--max-reference-length" or "--max-attempts") ||
                i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, missing or duplicate scenario option.");
        }
        if (!options.TryGetValue("--seed", out string? seedText) ||
            !ulong.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed) ||
            seed.ToString(CultureInfo.InvariantCulture) != seedText)
            throw new ArgumentException("--seed must be a canonical UInt64 decimal value.");
        if (!options.TryGetValue("--out", out string? output) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--out <new-file> is required.");
        int Positive(string key, int fallback)
        {
            if (!options.TryGetValue(key, out string? value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
                throw new ArgumentException($"{key} must be a positive Int32.");
            return parsed;
        }
        var scenario = ScenarioService.Generate(seed, Positive("--max-ticks", 512),
            Positive("--max-reference-length", 128), Positive("--max-attempts", 16));
        ScenarioService.Write(output, scenario);
        WriteSummary(ScenarioService.Validate(scenario));
        return 0;
    }
    catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
    catch (ScenarioGenerationException error)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Reason, seed = error.Seed.ToString(CultureInfo.InvariantCulture),
            generator = error.Generator, generation_attempts = error.Attempts }));
        return 1;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or AgentGame.Protocol.ProtocolException)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message }));
        return 1;
    }
}
if (args[0] == "run")
{
    try
    {
        int separator = Array.IndexOf(args, "--");
        if (separator < 0 || separator + 1 >= args.Length) throw new ArgumentException("run requires -- <executable> [arguments...].");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < separator; i++)
        {
            if (args[i] is "--headless" or "--observer-stdout" or "--tui")
            {
                if (!options.TryAdd(args[i], "true")) throw new ArgumentException("Duplicate run flag.");
            }
            else if (args[i] is not ("--scenario" or "--handshake-ms" or "--decision-ms" or "--record") ||
                i + 1 >= separator || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[++i]))
                throw new ArgumentException("Unknown, missing or duplicate run option.");
        }
        if (!options.TryGetValue("--scenario", out string? path) || string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("--scenario <file> is required.");
        // --headless means "attach no terminal observer". Until the M5 terminal exists it is also the
        // default, but it stays explicit and contradictory with streaming so the two cannot be confused.
        if (options.ContainsKey("--headless") && options.ContainsKey("--observer-stdout"))
            throw new ArgumentException("--headless cannot be combined with --observer-stdout.");
        if (options.ContainsKey("--tui") && (options.ContainsKey("--headless") || options.ContainsKey("--observer-stdout")))
            throw new ArgumentException("--tui cannot be combined with --headless or --observer-stdout.");
        if (options.ContainsKey("--tui") && Console.IsOutputRedirected)
            throw new ArgumentException("--tui requires an interactive terminal; use --headless or --observer-stdout when redirecting.");
        TimeSpan Milliseconds(string key, int fallback)
        {
            if (!options.TryGetValue(key, out string? value)) return TimeSpan.FromMilliseconds(fallback);
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
                throw new ArgumentException($"{key} must be a positive Int32.");
            return TimeSpan.FromMilliseconds(number);
        }
        var timeouts = new AgentTimeouts { Handshake = Milliseconds("--handshake-ms", 5000), Decision = Milliseconds("--decision-ms", 30000) };
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var command = new AgentCommand(args[separator + 1], args[(separator + 2)..]);
            ConsoleObserverSink? sink = null;
            TerminalObserverSink? terminal = null;
            using Stream output = Console.OpenStandardOutput();
            bool stream = options.ContainsKey("--observer-stdout");
            bool tui = options.ContainsKey("--tui");
            Action<AgentGame.Runtime.Observer.ObserverHub>? ready = null;
            if (stream) ready = hub => sink = new ConsoleObserverSink(hub.Register(), output);
            else if (tui) ready = hub => terminal = new TerminalObserverSink(hub.Register(), output);
            var runOptions = new RunOptions { RecordPath = options.GetValueOrDefault("--record"), ObserverReady = ready };
            RunResult result;
            try
            {
                result = await GameRunner.RunAsync(ScenarioService.Read(path), command, timeouts, cancellation.Token, runOptions);
                if (sink is not null)
                {
                    try { await sink.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { Console.Error.WriteLine("{\"observer_error\":\"output_timeout\"}"); }
                }
                if (terminal is not null)
                {
                    try { await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { Console.Error.WriteLine("{\"observer_error\":\"output_timeout\"}"); }
                }
            }
            finally
            {
                if (sink is not null) await sink.DisposeAsync();
                if (terminal is not null) await terminal.DisposeAsync();
            }
            if (sink?.Error is not null) Console.Error.WriteLine(JsonSerializer.Serialize(new { observer_error = sink.Error }));
            if (terminal?.Error is not null) Console.Error.WriteLine(JsonSerializer.Serialize(new { observer_error = terminal.Error }));
            if (stream || tui) Console.Error.WriteLine(ProtocolJson.EncodeLine(result));
            else await ConsoleObserverSink.WriteLineAsync(output, ProtocolJson.EncodeLine(result), TimeSpan.FromSeconds(1));
            return result.Kind == "cancelled" ? 130 : result.Kind == "execution_error" ? 1 : 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProtocolException or TimeoutException)
    { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message })); return 1; }
}
if (args[0] == "replay")
{
    try
    {
        string? record = null; bool tui = false; double speed = 1.0;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tui": tui = true; break;
                case "--speed":
                    if (i + 1 >= args.Length ||
                        !double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out speed) ||
                        speed is < 0.25 or > 16.0)
                        throw new ArgumentException("--speed must be a number between 0.25 and 16.");
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown replay option '{args[i]}'.");
                    if (record is not null) throw new ArgumentException("replay takes exactly one record file.");
                    record = args[i];
                    break;
            }
        }
        if (record is null) throw new ArgumentException("Usage: agent-game replay <jsonl> [--tui] [--speed 1.0]");
        if (!tui && speed != 1.0) throw new ArgumentException("--speed requires --tui.");
        if (tui)
        {
            if (Console.IsOutputRedirected)
                throw new ArgumentException("--tui requires an interactive terminal; omit it to export the recorded envelopes as JSONL.");
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler replayer = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += replayer;
            try { return await ReplayPlayer.PlayAsync(record, speed, cancellation.Token); }
            finally { Console.CancelKeyPress -= replayer; }
        }
        using Stream output = Console.OpenStandardOutput();
        using var reader = new ReplayReader(record);
        await ConsoleObserverSink.WriteLineAsync(output, ObserverCodec.Encode(reader.Header.InitialSnapshot), TimeSpan.FromSeconds(1));
        while (reader.ReadNext() is { } item)
        {
            object? envelope = item switch { ReplayStatusRecord status => status.ObserverStatus,
                ReplayStepRecord step => step.ObserverBatch, _ => null };
            if (envelope is not null)
                await ConsoleObserverSink.WriteLineAsync(output, ObserverCodec.Encode(envelope), TimeSpan.FromSeconds(1));
        }
        Console.Error.WriteLine(ProtocolJson.EncodeLine(new ReplaySummary(reader.Status, reader.LastTick, reader.LastSeq, reader.LineNumber)));
        return reader.Status == "incomplete" ? 1 : 0;
    }
    catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
    catch (ReplayFormatException error)
    { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Code, line = error.LineNumber })); return 1; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProtocolException or TimeoutException)
    { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message })); return 1; }
}
if (args[0] == "verify")
{
    if (args.Length != 2) { Console.Error.WriteLine("Usage: agent-game verify <jsonl>"); return 2; }
    try
    {
        using Stream output = Console.OpenStandardOutput();
        VerificationResult result = ReplayService.Verify(args[1]);
        await ConsoleObserverSink.WriteLineAsync(output, ProtocolJson.EncodeLine(result), TimeSpan.FromSeconds(1));
        return result.Valid ? 0 : 1;
    }
    catch (ReplayFormatException error)
    { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Code, line = error.LineNumber })); return 1; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProtocolException or TimeoutException)
    { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message })); return 1; }
}
if (args[0] == "play")
{
    try
    {
        string? scenarioPath = null, recordPath = null;
        bool plain = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--scenario requires a path.");
                    scenarioPath = args[++i]; break;
                case "--record":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--record requires a new file path.");
                    recordPath = args[++i]; break;
                case "--plain": plain = true; break;
                default: throw new ArgumentException($"Unknown play option '{args[i]}'.");
            }
        }
        if (scenarioPath is null) throw new ArgumentException("play requires --scenario <file>.");
        // A terminal observer needs an interactive terminal; with redirected streams the
        // prompts still work (they go to stderr) but no frames are drawn.
        bool interactive = !plain && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var input = new HumanInput();
            using Stream output = Console.OpenStandardOutput();
            TerminalObserverSink? terminal = null;
            var runOptions = new RunOptions
            {
                RecordPath = recordPath,
                ObserverReady = interactive
                    ? hub => terminal = new TerminalObserverSink(hub.Register(), output, interactiveControls: false)
                    : null
            };
            RunResult result;
            try
            {
                result = await HumanSession.PlayAsync(ScenarioService.Read(scenarioPath), (observation, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    ActionRequestDto? action = input.ReadStep(observation, Console.Error);
                    if (action is null) throw new OperationCanceledException("The player ended the episode.");
                    return Task.FromResult(action);
                }, cancellationToken: cancellation.Token, options: runOptions);
                if (terminal is not null)
                {
                    try { await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { Console.Error.WriteLine("{\"observer_error\":\"output_timeout\"}"); }
                }
            }
            finally { if (terminal is not null) await terminal.DisposeAsync(); }
            if (terminal?.Error is not null) Console.Error.WriteLine(JsonSerializer.Serialize(new { observer_error = terminal.Error }));
            if (interactive) Console.Error.WriteLine(ProtocolJson.EncodeLine(result));
            else await ConsoleObserverSink.WriteLineAsync(output, ProtocolJson.EncodeLine(result), TimeSpan.FromSeconds(1));
            return result.Kind == "cancelled" ? 130 : result.Kind == "execution_error" ? 1 : 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProtocolException or TimeoutException)
    { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message })); return 1; }
}
Console.Error.WriteLine($"Unknown command or arguments: '{args[0]}'. Use --help.");
return 2;

static void WriteSummary(ScenarioSummary summary) => Console.WriteLine(JsonSerializer.Serialize(new
{
    valid = summary.Valid, error = summary.Error, generator = summary.Generator, seed = summary.Seed,
    generation_attempts = summary.GenerationAttempts, reference_length = summary.ReferenceLength, initial_hash = summary.InitialHash
}));
