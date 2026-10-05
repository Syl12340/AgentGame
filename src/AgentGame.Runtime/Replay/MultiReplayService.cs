using AgentGame.Core;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;
using AgentGame.Runtime.Observer;

namespace AgentGame.Runtime.Replay;

/// <summary>
/// replay/2 playback and independent verification. <see cref="Export"/> only extracts recorded
/// Observer envelopes for the single selected view without constructing a Core game.
/// <see cref="Verify"/> re-executes committed actions with <see cref="MultiGame"/> and checks every
/// hash / outcome / events / patch against the selected-view <see cref="MultiObserverProjection"/>.
/// </summary>
public static class MultiReplayService
{
    /// <summary>Playback extracts recorded Observer envelopes without constructing a Core game.</summary>
    public static ReplaySummary Export(string path, Action<object> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var reader = new MultiReplayReader(path);
        consume(reader.Header.InitialSnapshot);
        while (reader.ReadNext() is { } record)
        {
            switch (record)
            {
                case MultiReplayStatusRecord status: consume(status.ObserverStatus); break;
                case MultiReplayStepRecord step: consume(step.ObserverBatch); break;
            }
        }
        return new(reader.Status, reader.LastTick, reader.LastSeq, reader.LineNumber);
    }

    /// <summary>
    /// Re-executes committed seat actions against the facility-zero/2 rules and checks the
    /// selected-view projection deterministically. Unsupported version headers stay playable but
    /// are refused here.
    /// </summary>
    public static VerificationResult Verify(string path)
    {
        MultiReplayReader? reader = null;
        MultiGame? game = null;
        string? hash = null;
        MultiObserverProjection? projection = null;
        try
        {
            reader = new MultiReplayReader(path);
            MultiReplayRunHeader header = reader.Header;
            if (header.AgentProtocol != M2Protocol.AgentProtocol || header.ObserverProtocol != M2Protocol.ObserverProtocol ||
                header.Rules != M2Protocol.RulesVersion || header.Scenario.Rules != M2Protocol.RulesVersion ||
                header.CoreEncoding != M2Protocol.CoreEncoding)
                return new(false, "unsupported_version", "unsupported_version", 1, 0, null);
            game = MultiGame.Create(MultiScenarioService.ToCoreScenario(header.Scenario));
            hash = MultiStateEncoding.Hash(game.Capture());
            var view = M2Protocol.ParseView(header.InitialSnapshot.View);
            projection = new MultiObserverProjection(game.Capture(), view.IsSpectator ? null : game.Observe(view.Seat!.Value),
                header.InitialSnapshot.View);
            if (!Same(projection.State, header.InitialSnapshot.State))
                return Failure("initial_snapshot_mismatch", 1);
            while (reader.ReadNext() is { } record)
            {
                if (record is not MultiReplayStepRecord step) continue;
                if (game.Episode is not null) return Failure("step_after_episode");
                MultiStepResult result = game.Step(step.Seat, MultiScenarioService.ToCoreAction(step.Action));
                hash = MultiStateEncoding.Hash(game.Capture());
                if (!string.Equals(hash, step.CoreHash, StringComparison.Ordinal)) return Failure("core_hash_mismatch");
                if (!Same(ToFeedback(result.Outcome), step.Outcome)) return Failure("outcome_mismatch");
                if (!Same(projection.Events(result), step.ObserverBatch.Events)) return Failure("events_mismatch");
                ObserverPatch patch = projection.Apply(game.Capture(), view.IsSpectator ? null : game.Observe(view.Seat!.Value));
                if (!Same(patch, step.ObserverBatch.Patch)) return Failure("observer_patch_mismatch");
            }
            if (reader.Status == "incomplete") return Failure("incomplete", status: "incomplete");
            return new(true, reader.Status, null, null, game.Tick, hash);
        }
        catch (ReplayFormatException error)
        {
            return new(false, "invalid", error.Code, error.LineNumber, game?.Tick ?? reader?.LastTick, hash);
        }
        catch (Exception error) when (error is ProtocolException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return new(false, "invalid", "invalid_rule_input", reader?.LineNumber ?? 1, game?.Tick ?? reader?.LastTick, hash);
        }
        finally { reader?.Dispose(); }

        VerificationResult Failure(string error, int? line = null, string status = "invalid") =>
            new(false, status, error, line ?? reader?.LineNumber, game?.Tick, hash);
    }

    private static bool Same<T>(T expected, T actual) => ProtocolJson.EncodeLine(expected) == ProtocolJson.EncodeLine(actual);

    private static ActionFeedback ToFeedback(MultiActionResult outcome) => new()
    {
        Status = outcome.Status switch
        {
            ActionStatus.Applied => ActionStatusDto.Applied, ActionStatus.Blocked => ActionStatusDto.Blocked,
            ActionStatus.NoEffect => ActionStatusDto.NoEffect,
            _ => throw new ArgumentException("Unknown Core outcome.")
        },
        Reason = outcome.Reason
    };
}