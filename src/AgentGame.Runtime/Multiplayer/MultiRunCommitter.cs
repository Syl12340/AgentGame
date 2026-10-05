using AgentGame.Core;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

namespace AgentGame.Runtime;

/// <summary>
/// The only publisher. Every append finishes before its envelope becomes visible.
/// Note: While steps publish all views in the session, the file stores only ONE selected view (default spectator) and actions.
/// Every seat's rule actions are retained in the selected-view archive.
/// </summary>
internal sealed class MultiRunCommitter : IAsyncDisposable
{
    private readonly IReplayWriter? _writer;
    private readonly MultiScenarioDto _scenario;
    private readonly MultiRunOptions _options;
    private bool _started, _broken;

    public MultiObserverSession Session { get; }
    public MultiSnapshot Committed { get; private set; }

    internal MultiRunCommitter(MultiScenarioDto scenario, MultiGame game, MultiRunOptions options)
    {
        if (options.RecordPath is not null && options.ReplayWriter is not null)
            throw new ArgumentException("Specify RecordPath or ReplayWriter, not both.");

        var parsed = M2Protocol.ParseView(options.RecordView);
        if (!parsed.IsSpectator && (parsed.Seat!.Value < 0 || parsed.Seat.Value >= scenario.Spawns.Length))
            throw new ArgumentException("Selected view seat out of range.");

        _scenario = scenario;
        _options = options;
        Committed = game.Capture();
        Session = new MultiObserverSession(Committed, Observations(game), options.RunId ?? Guid.NewGuid().ToString("N"));
        _writer = options.ReplayWriter ?? (options.RecordPath is null ? null : new MultiReplayFileWriter(options.RecordPath));
    }

    public async Task StartAsync(string name)
    {
        if (_started) return;
        var header = new MultiReplayRunHeader
        {
            AgentName = name,
            Scenario = _scenario,
            Generator = _scenario.Generator,
            InitialSnapshot = (MultiSnapshotMessage)Session.GetHub(_options.RecordView).CurrentSnapshot()
        };
        await AppendAsync(header);
        _started = true;
        _options.ObserverReady?.Invoke(Session);
    }

    public async Task StatusAsync(AgentStatusDto status)
    {
        var commit = Session.PrepareStatus(_options.RecordView, status);
        var envelope = (MultiAgentStatusMessage)commit.Batches[0];
        await AppendAsync(new MultiReplayStatusRecord { ObserverStatus = envelope });
        Session.Commit(commit);
    }

    public async Task StepAsync(MultiGame game, MultiStepResult step, ActionRequestDto action)
    {
        var frozenAction = action with { };
        MultiSnapshot next = game.Capture();
        var commit = Session.PrepareStep(next, Observations(game), step);

        MultiStepBatchMessage? observerBatch = null;
        foreach (var batchObj in commit.Batches)
        {
            if (batchObj is MultiStepBatchMessage batch && batch.View == _options.RecordView)
            {
                observerBatch = batch;
                break;
            }
        }

        if (observerBatch is null)
            throw new InvalidOperationException("Selected view batch not found.");

        await AppendAsync(new MultiReplayStepRecord
        {
            Seq = observerBatch.Seq,
            Tick = observerBatch.Tick,
            Seat = step.Seat,
            Action = frozenAction,
            Outcome = new()
            {
                Status = step.Outcome.Status switch
                {
                    ActionStatus.Applied => ActionStatusDto.Applied,
                    ActionStatus.Blocked => ActionStatusDto.Blocked,
                    _ => ActionStatusDto.NoEffect
                },
                Reason = step.Outcome.Reason
            },
            CoreHash = MultiStateEncoding.Hash(next),
            ObserverBatch = observerBatch
        });

        Session.Commit(commit);
        Committed = next;
    }

    public async Task FinishAsync(string name, bool completed, object? stats = null)
    {
        if (_broken) return;
        await StartAsync(name);
        await StatusAsync(completed ? AgentStatusDto.Stopped : AgentStatusDto.Errored);
        await AppendAsync(new MultiReplayRunFooter
        {
            Status = completed ? "completed" : "aborted",
            LastTick = Committed.Tick,
            Result = Committed.Episode is null ? null : MultiScenarioService.ToEpisode(Committed.Episode),
            Stats = stats
        });
    }

    private async Task AppendAsync(object record)
    {
        if (_broken) throw new InvalidOperationException("Authority recording already failed.");
        if (_writer is null) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _writer.AppendAsync(record, deadline.Token).AsTask().WaitAsync(deadline.Token);
        }
        catch (Exception error) { _broken = true; throw new RecordingException(error); }
    }

    private MultiObservation[] Observations(MultiGame game) =>
        Enumerable.Range(0, _scenario.Spawns.Length).Select(game.Observe).ToArray();

    public async ValueTask DisposeAsync()
    {
        Session.Complete();
        if (_writer is null) return;
        try { await _writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception error) { throw new RecordingException(error); }
    }
}
