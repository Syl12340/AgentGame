using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

namespace AgentGame.Runtime;

internal sealed class RecordingException(Exception inner) : Exception("Authority recording failed: " + inner.Message, inner);

/// <summary>The only publisher. Every append finishes before its envelope becomes visible.</summary>
internal sealed class RunCommitter : IAsyncDisposable
{
    private readonly IReplayWriter? _writer;
    private readonly ScenarioDto _scenario;
    private readonly ObserverProjection _projection;
    private readonly Action<ObserverHub>? _ready;
    private bool _started, _broken;
    public ObserverHub Hub { get; }
    public CoreSnapshot Committed { get; private set; }
    public string? LastRequestId { get; private set; }
    public ActionRequestDto? LastAction { get; private set; }
    public RunCommitter(ScenarioDto scenario, Game game, RunOptions options)
    {
        if (options.RecordPath is not null && options.ReplayWriter is not null)
            throw new ArgumentException("Specify RecordPath or ReplayWriter, not both.");
        _scenario = scenario;
        Committed = game.Capture();
        _projection = new(Committed, game.Observe());
        Hub = new(new SnapshotMessage { RunId = options.RunId ?? Guid.NewGuid().ToString("N"),
            AgentStatus = AgentStatusDto.Waiting, State = _projection.State });
        _ready = options.ObserverReady;
        _writer = options.ReplayWriter ?? (options.RecordPath is null ? null : new ReplayFileWriter(options.RecordPath));
    }
    public async Task StartAsync(string? name)
    {
        if (_started) return;
        await AppendAsync(new ReplayRunHeader { AgentName = name ?? "", Scenario = _scenario,
            Generator = _scenario.Generator, InitialSnapshot = Hub.CurrentSnapshot() });
        _started = true;
        _ready?.Invoke(Hub);
    }
    public async Task StatusAsync(AgentStatusDto status)
    {
        SnapshotMessage previous = Hub.CurrentSnapshot();
        var message = new AgentStatusMessage { RunId = previous.RunId, Seq = checked(previous.BaseSeq + 1),
            Tick = previous.Tick, AgentStatus = status };
        var publication = Hub.Prepare(message);
        await AppendAsync(new ReplayStatusRecord { ObserverStatus = message });
        Hub.Publish(publication);
    }
    public async Task StepAsync(Game game, StepResult step, ActionRequestDto action, string requestId)
    {
        SnapshotMessage previous = Hub.CurrentSnapshot();
        CoreSnapshot next = game.Capture();
        var message = new StepBatchMessage { RunId = previous.RunId, Seq = checked(previous.BaseSeq + 1),
            Tick = next.Tick, AgentStatus = AgentStatusDto.ActionReceived,
            Events = ObserverProjection.Events(step), Patch = _projection.Apply(game.Observe()) };
        var publication = Hub.Prepare(message);
        await AppendAsync(new ReplayStepRecord { Seq = message.Seq, Tick = message.Tick, Action = action,
            Outcome = new() { Status = step.Outcome.Status switch { ActionStatus.Applied => ActionStatusDto.Applied,
                ActionStatus.Blocked => ActionStatusDto.Blocked, _ => ActionStatusDto.NoEffect }, Reason = step.Outcome.Reason },
            CoreHash = StateEncoding.Hash(next), ObserverBatch = message });
        Hub.Publish(publication);
        Committed = next;
        LastAction = action with { };
        LastRequestId = requestId;
    }
    public async Task FinishAsync(string? name, bool completed)
    {
        if (_broken) return;
        await StartAsync(name);
        await StatusAsync(completed ? AgentStatusDto.Stopped : AgentStatusDto.Errored);
        await AppendAsync(new ReplayRunFooter { Status = completed ? "completed" : "aborted",
            LastTick = Committed.Tick, Result = Committed.Episode is null ? null : new()
            { Kind = Committed.Episode.Kind == EpisodeEndKind.Success ? EpisodeKindDto.Success : EpisodeKindDto.TurnLimit } });
    }
    private async Task AppendAsync(object record)
    {
        if (_broken) throw new InvalidOperationException("Authority recording already failed.");
        if (_writer is null) return;
        try
        {
            // A stuck output must not keep the child process alive indefinitely.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _writer.AppendAsync(record, deadline.Token).AsTask().WaitAsync(deadline.Token);
        }
        catch (Exception error) { _broken = true; throw new RecordingException(error); }
    }
    public async ValueTask DisposeAsync()
    {
        Hub.Complete();
        if (_writer is null) return;
        try { await _writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception error) { throw new RecordingException(error); }
    }
}
