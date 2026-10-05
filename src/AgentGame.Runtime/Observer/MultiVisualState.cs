using AgentGame.Protocol;

namespace AgentGame.Runtime.Observer;

/// <summary>
/// Transactional observer/2 reducer. It reuses the single-seat <see cref="VisualState"/> as an
/// internal reducer after adapting a v2 snapshot to the v1 wire shape (protocol observer/1,
/// view agent), and it forwards every v2 envelope as the matching v1 envelope. The original v2
/// view and run_id are preserved externally so the emitted snapshot stays observer/2.
///
/// Validation order: inputs are first frozen with a strict <see cref="MultiObserverCodec"/>
/// round trip (which rejects any v1 DTO and validates protocol/view/privacy/state/patch), then
/// the original v2 view and run_id are checked against the reducer, then the v1 reducer's
/// ordered-stream and rule-shape checks run. The v1 reducer is never modified and never leaks
/// a partially applied patch: a rejected <see cref="Apply"/> leaves the previous snapshot intact.
/// </summary>
public sealed class MultiVisualState
{
    private readonly string _view;
    private readonly string _runId;
    private readonly VisualState _reducer;

    private MultiVisualState(string view, string runId, VisualState reducer)
    {
        _view = view;
        _runId = runId;
        _reducer = reducer;
    }

    public static MultiVisualState FromSnapshot(MultiSnapshotMessage snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Strict v2 round trip: rejects any v1 DTO and validates view/privacy/state before we
        // go near the reducer. Returns an owned deep clone.
        var owned = (MultiSnapshotMessage)MultiObserverCodec.Parse(MultiObserverCodec.Encode(snapshot));
        var v1 = new SnapshotMessage
        {
            Type = "snapshot",
            Protocol = ProtocolLimits.ObserverProtocol,
            RunId = owned.RunId,
            View = ProtocolLimits.ViewAgent,
            BaseSeq = owned.BaseSeq,
            Tick = owned.Tick,
            AgentStatus = owned.AgentStatus,
            State = owned.State,
        };
        return new MultiVisualState(owned.View, owned.RunId, VisualState.FromSnapshot(v1));
    }

    public MultiSnapshotMessage Snapshot()
    {
        SnapshotMessage v1 = _reducer.Snapshot();
        var v2 = new MultiSnapshotMessage
        {
            Type = "snapshot",
            Protocol = M2Protocol.ObserverProtocol,
            RunId = v1.RunId,
            View = _view,
            BaseSeq = v1.BaseSeq,
            Tick = v1.Tick,
            AgentStatus = v1.AgentStatus,
            State = v1.State,
        };
        // Deep clone through the strict codec; also re-validates per-view seat/privacy entities.
        return (MultiSnapshotMessage)MultiObserverCodec.Parse(MultiObserverCodec.Encode(v2));
    }

    public void Apply(object envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        // Freeze v2; rejects any v1 DTO before validation.
        object owned = MultiObserverCodec.Parse(MultiObserverCodec.Encode(envelope));
        switch (owned)
        {
            case MultiAgentStatusMessage status:
                CheckIdentity(status.Protocol, status.RunId, status.View);
                _reducer.Apply(ToV1(status));
                break;
            case MultiStepBatchMessage step:
                CheckIdentity(step.Protocol, step.RunId, step.View);
                _reducer.Apply(ToV1(step));
                break;
            default:
                throw new ProtocolException("Reducer accepts only v2 step_batch and agent_status envelopes.");
        }
    }

    private void CheckIdentity(string protocol, string runId, string view)
    {
        if (protocol != M2Protocol.ObserverProtocol)
            throw new ProtocolException("Observer/2 stream protocol changed.");
        if (string.IsNullOrWhiteSpace(runId) || runId != _runId)
            throw new ProtocolException("Observer/2 stream run_id changed; a new snapshot is required.");
        if (view != _view)
            throw new ProtocolException("Observer/2 stream view changed; a new snapshot is required.");
    }

    private static AgentStatusMessage ToV1(MultiAgentStatusMessage value) => new()
    {
        Type = "agent_status",
        Protocol = ProtocolLimits.ObserverProtocol,
        RunId = value.RunId,
        View = ProtocolLimits.ViewAgent,
        Seq = value.Seq,
        Tick = value.Tick,
        AgentStatus = value.AgentStatus,
    };

    private static StepBatchMessage ToV1(MultiStepBatchMessage value) => new()
    {
        Type = "step_batch",
        Protocol = ProtocolLimits.ObserverProtocol,
        RunId = value.RunId,
        View = ProtocolLimits.ViewAgent,
        Seq = value.Seq,
        Tick = value.Tick,
        AgentStatus = value.AgentStatus,
        Events = value.Events,
        Patch = value.Patch,
    };
}