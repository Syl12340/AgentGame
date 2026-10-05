using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentGame.Protocol;
using AgentGame.Runtime.Agents;

#nullable enable

/// <summary>
/// M7 focused checks for the multi-seat decision sources: the real subprocess agent/2 handshake and
/// action path, protocol / request-id faults, the decision-silence timeout, the cleanup record, the
/// in-process human (seat-owned observation only) and wait sources, v1 process-session regression,
/// and source seat/lifetime validation. These checks exercise the sources directly and do not depend
/// on the (separately owned) multi-seat runner.
/// </summary>
internal static class M7SeatSourceChecks
{
    private const string FixtureName = "v2-fixture-agent";
    private const string V1FixtureName = "fault-agent";

    public static async Task<int> RunAsync(string root)
    {
        string python = Environment.GetEnvironmentVariable("AGENT_GAME_PYTHON") ?? "python";
        string v2Fixture = Path.Combine(root, "tests", "Fixtures", "Agents", "v2_fixture_agent.py");
        string v1Fixture = Path.Combine(root, "tests", "Fixtures", "Agents", "fault_agent.py");
        string directory = Path.Combine(root, "artifacts", "m73-seat-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("V2 子进程握手返回名称并按要求 wait 决策且写入观察日志", () => V2WaitAsync(python, v2Fixture, directory)),
            ("V2 非 agent/2 协议的 ready 拒绝握手并立即释放子进程", () => V2WrongReadyAsync(python, v2Fixture, directory)),
            ("V2 不匹配的 request_id 拒绝决策且关闭时回收进程", () => V2WrongRequestAsync(python, v2Fixture, directory)),
            ("V2 决策静默在决定超时内失败并回收进程", () => V2SilentAsync(python, v2Fixture, directory)),
            ("V1 进程会话回归：真实子进程中握手与决策仍可用", () => V1RegressionAsync(python, v1Fixture, directory)),
            ("Human 源只把本座位观察交给回调并拒绝他座观察", HumanOwnedSeatAsync),
            ("Wait 源始终返回合法 wait 动作", () => WaitSourceAsync()),
            ("源越界座位先于进程启动被拒绝", () => SeatValidationAsync(python, v2Fixture, directory)),
            ("源关闭后再次使用被拒绝", () => CloseOnceAsync(python, v2Fixture, directory)),
        };

        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M7 Seat sources: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static async Task V2WaitAsync(string python, string fixture, string directory)
    {
        string log = Path.Combine(directory, "v2-wait.log");
        var command = new AgentCommand(python, new[] { "-u", fixture, "--mode", "wait", "--log", log });
        var source = new ProcessMultiSeatSource(command, ShortDecisionTimeouts());

        string name = await source.StartAsync(seat: 0, seatCount: 2, default);
        Equal(FixtureName, name);

        string requestId = "r0";
        var action = await source.DecideAsync(Observation(0, 1L, requestId), default);
        Equal(ActionTypeDto.Wait, action.Type);

        await source.EndAsync(new AgentV2EpisodeEndMessage
        {
            RequestId = requestId,
            Result = new EpisodeBody { Kind = EpisodeKindDto.TurnLimit },
            Observation = Observation(0, 1L, requestId).Observation,
        }, default);

        MultiSeatCleanup cleanup = await source.CloseAsync(graceful: true);
        Require(cleanup.ProcessId is not null and > 0, "Cleanup did not report a real process id.");
        Equal(0, cleanup.ExitCode);
        Equal(false, cleanup.ForcedTermination);
        Require(cleanup.Error is null, $"Cleanup reported an error: {cleanup.Error?.Detail}");
        Require(cleanup.StderrTail is not null, "Cleanup stderr tail must be a string.");
        Require(File.Exists(log), "V2 agent did not write its observation log.");
        string[] records = File.ReadAllLines(log);
        Require(records.Length >= 1, "V2 agent wrote no observation records.");
        using var record = JsonDocument.Parse(records[0]);
        Equal(requestId, record.RootElement.GetProperty("request_id").GetString());
        Equal(0, record.RootElement.GetProperty("seat").GetInt32());
    }

    private static async Task V2WrongReadyAsync(string python, string fixture, string directory)
    {
        var source = new ProcessMultiSeatSource(new AgentCommand(python, new[] { "-u", fixture, "--mode", "wrong_ready" }), ShortDecisionTimeouts());
        await RejectAsync(() => source.StartAsync(seat: 0, seatCount: 2, default),
            "handshake with a non agent/2 ready must fail");
        MultiSeatCleanup cleanup = await source.CloseAsync(graceful: false);
        Require(cleanup.ProcessId is > 0, "Start-failed source lost the child's cleanup diagnostics.");
    }

    private static async Task V2WrongRequestAsync(string python, string fixture, string directory)
    {
        var source = new ProcessMultiSeatSource(new AgentCommand(python, new[] { "-u", fixture, "--mode", "wrong_request" }), ShortDecisionTimeouts());
        await source.StartAsync(seat: 1, seatCount: 2, default);
        await RejectAsync(() => source.DecideAsync(Observation(1, 1L, "req-1"), default),
            "a mismatched action request_id must fail the decision");
        MultiSeatCleanup cleanup = await source.CloseAsync(graceful: false);
        Require(cleanup.ExitCode is not 0, "Faulty agent cleaned up with a non-zero or absent lifecycle.");
        Require(cleanup.Error is null, $"Close-after-failure should not add a new error: {cleanup.Error?.Detail}");
    }

    private static async Task V2SilentAsync(string python, string fixture, string directory)
    {
        var timeouts = new AgentTimeouts { Decision = TimeSpan.FromMilliseconds(1200) };
        var source = new ProcessMultiSeatSource(new AgentCommand(python, new[] { "-u", fixture, "--mode", "silent" }), timeouts);
        await source.StartAsync(seat: 0, seatCount: 2, default);
        await RejectAsync(() => source.DecideAsync(Observation(0, 1L, "r-silent"), default),
            "a silent decision must time out");
        await source.CloseAsync(graceful: false);
    }

    private static async Task V1RegressionAsync(string python, string fixture, string directory)
    {
        // Real v1 (agent/1) subprocess through the preserved ProcessAgentSession V1 methods.
        var command = new AgentCommand(python, new[] { "-u", fixture, "--mode", "wait" });
        var session = ProcessAgentSession.Start(command, new AgentTimeouts());
        try
        {
            ReadyResponse ready = await session.HandshakeAsync(default);
            Equal(V1FixtureName, ready.Name);
            Equal(ProtocolLimits.AgentProtocol, ready.Protocol);

            var message = new ObservationMessage
            {
                RequestId = "v1-0",
                Tick = 0,
                Observation = new AgentObservationBody
                {
                    Position = new PointDto { X = 1, Y = 1 },
                    Mission = MissionPhaseDto.FindKey,
                },
            };
            ActionResponse action = await session.DecideAsync(message, default);
            Equal("v1-0", action.RequestId);
            Equal(ActionTypeDto.Wait, action.Action.Type);

            await session.EndAsync(new EpisodeEndMessage
            {
                RequestId = "v1-0",
                Result = new EpisodeBody { Kind = EpisodeKindDto.TurnLimit },
                Observation = message.Observation,
            }, default);
        }
        finally
        {
            await session.CloseAsync(graceful: true);
        }
    }

    private static Task HumanOwnedSeatAsync()
    {
        int seat = 1;
        var seen = new List<int>();
        var source = new HumanMultiSeatSource(async (observation, _) =>
        {
            seen.Add(observation.Seat);
            return new ActionRequestDto { Type = ActionTypeDto.Wait };
        }, name: "human-owner");

        return RunAll(
            async () =>
            {
                Equal("human-owner", await source.StartAsync(seat, 2, default));
                var action = await source.DecideAsync(Observation(seat, 2L, "h-2"), default);
                Equal(ActionTypeDto.Wait, action.Type);
                Equal(seat, seen.Single());
                await RejectAsync(() => source.DecideAsync(Observation(0, 3L, "foreign"), default),
                    "a foreign-seat observation must be rejected by the human source");
                await source.EndAsync(new AgentV2EpisodeEndMessage
                {
                    RequestId = "h-2",
                    Result = new EpisodeBody { Kind = EpisodeKindDto.Success },
                    Observation = Observation(seat, 2L, "h-2").Observation,
                }, default);
                var cleanup = await source.CloseAsync(graceful: true);
                Require(cleanup.ProcessId is null && cleanup.ExitCode is null, "Human cleanup must be process-free.");
            });
    }

    private static Task WaitSourceAsync()
    {
        var source = new WaitMultiSeatSource();
        return RunAll(async () =>
        {
            Equal("wait", await source.StartAsync(0, 2, default));
            var action = await source.DecideAsync(Observation(0, 4L, "w-4"), default);
            Equal(ActionTypeDto.Wait, action.Type);
            await source.EndAsync(new AgentV2EpisodeEndMessage
            {
                RequestId = "w-4",
                Result = new EpisodeBody { Kind = EpisodeKindDto.Success },
                Observation = Observation(0, 4L, "w-4").Observation,
            }, default);
            var cleanup = await source.CloseAsync(graceful: true);
            Require(cleanup.ProcessId is null, "Wait source must report no process.");
        });
    }

    private static async Task SeatValidationAsync(string python, string fixture, string directory)
    {
        // Out-of-range seats must be rejected before any process starts for each source kind.
        foreach (IMultiSeatSource source in new IMultiSeatSource[]
        {
            new ProcessMultiSeatSource(new AgentCommand(python, new[] { "-u", fixture, "--mode", "wait" })),
            new HumanMultiSeatSource((_, _) => Task.FromResult(new ActionRequestDto { Type = ActionTypeDto.Wait })),
            new WaitMultiSeatSource(),
        })
        {
            await RejectAsync(() => source.StartAsync(seat: 2, seatCount: 2, default), "out-of-range seat must be rejected");
            await RejectAsync(() => source.StartAsync(seat: -1, seatCount: 2, default), "negative seat must be rejected");
            await RejectAsync(() => source.StartAsync(seat: 0, seatCount: 5, default), "seat count > 4 must be rejected");
            await source.CloseAsync(graceful: true);
        }
    }

    private static async Task CloseOnceAsync(string python, string fixture, string directory)
    {
        var source = new ProcessMultiSeatSource(new AgentCommand(python, new[] { "-u", fixture, "--mode", "wait" }));
        await source.StartAsync(0, 2, default);
        await source.CloseAsync(graceful: true);
        await RejectAsync(() => source.StartAsync(0, 2, default), "start after close must be rejected");
        await RejectAsync(() => source.CloseAsync(graceful: true), "double close must be rejected");

        var wait = new WaitMultiSeatSource();
        await wait.CloseAsync(graceful: true);
        await RejectAsync(() => wait.DecideAsync(Observation(0, 1L, "late"), default), "decide after close must be rejected");
    }

    // ----- helpers -----

    private static AgentV2ObservationMessage Observation(int seat, long tick, string requestId) => new()
    {
        RequestId = requestId,
        Tick = tick,
        Seat = seat,
        Observation = new AgentObservationBody
        {
            Position = new PointDto { X = 1, Y = 1 },
            Tiles =
            [
                new AgentTile { X = 1, Y = 1, Terrain = TerrainDto.Floor, IsExit = false },
            ],
            Inventory = [],
            Mission = MissionPhaseDto.FindKey,
        },
    };

    private static AgentTimeouts ShortDecisionTimeouts() => new() { Decision = TimeSpan.FromSeconds(4) };

    private static async Task RunAll(params Func<Task>[] actions)
    {
        foreach (Func<Task> action in actions) await action();
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is AgentSessionException or ProtocolException
            or ArgumentOutOfRangeException or ArgumentException or InvalidOperationException) { return; }
        throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
