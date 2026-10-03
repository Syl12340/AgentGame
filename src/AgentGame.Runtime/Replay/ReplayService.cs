using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime.Observer;

namespace AgentGame.Runtime.Replay;

public sealed record ReplaySummary(string Status, long LastTick, long LastSeq, int Lines);
public sealed record VerificationResult(bool Valid, string Status, string? Error, int? Line, long? Tick, string? CoreHash);

public static class ReplayService
{
    /// <summary>Playback extracts recorded Observer envelopes without constructing a Core game.</summary>
    public static ReplaySummary Export(string path, Action<object> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var reader = new ReplayReader(path);
        consume(reader.Header.InitialSnapshot);
        while (reader.ReadNext() is { } record)
        {
            switch (record)
            {
                case ReplayStatusRecord status: consume(status.ObserverStatus); break;
                case ReplayStepRecord step: consume(step.ObserverBatch); break;
            }
        }
        return new(reader.Status, reader.LastTick, reader.LastSeq, reader.LineNumber);
    }

    /// <summary>Re-executes committed actions against the supported rules and checks deterministic output.</summary>
    public static VerificationResult Verify(string path)
    {
        ReplayReader? reader = null;
        Game? game = null;
        string? hash = null;
        try
        {
            reader = new ReplayReader(path);
            ReplayRunHeader header = reader.Header;
            if (header.Rules != ProtocolLimits.RulesVersion || header.CoreEncoding != ProtocolLimits.CoreEncoding ||
                header.Scenario.Rules != ProtocolLimits.RulesVersion)
                return new(false, "unsupported_version", "unsupported_version", 1, 0, null);
            game = Game.Create(ScenarioService.ToCoreScenario(header.Scenario));
            hash = StateEncoding.Hash(game.Capture());
            var projection = new ObserverProjection(game.Capture(), game.Observe());
            if (!Same(projection.State, header.InitialSnapshot.State))
                return Failure("initial_snapshot_mismatch", 1);
            while (reader.ReadNext() is { } record)
            {
                if (record is not ReplayStepRecord step) continue;
                if (game.Episode is not null) return Failure("step_after_episode");
                StepResult result = game.Step(ToCore(step.Action));
                hash = StateEncoding.Hash(game.Capture());
                if (!string.Equals(hash, step.CoreHash, StringComparison.Ordinal)) return Failure("core_hash_mismatch");
                if (!Same(ToFeedback(result.Outcome), step.Outcome)) return Failure("outcome_mismatch");
                if (!Same(ObserverProjection.Events(result), step.ObserverBatch.Events)) return Failure("events_mismatch");
                ObserverPatch patch = projection.Apply(game.Observe());
                if (!Same(patch, step.ObserverBatch.Patch)) return Failure("observer_patch_mismatch");
                // No view_hash encoding is frozen for observer/1. It remains optional metadata.
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

    internal static GameAction ToCore(ActionRequestDto action) => action.Type switch
    {
        ActionTypeDto.Move => GameAction.Move(ToCore(action.Direction!.Value)),
        ActionTypeDto.Interact => GameAction.Interact(ToCore(action.Direction!.Value)),
        ActionTypeDto.Pickup => GameAction.Pickup(),
        ActionTypeDto.Wait => GameAction.Wait(),
        _ => throw new ArgumentException("Unknown replay action.")
    };

    private static Direction ToCore(DirectionDto direction) => direction switch
    {
        DirectionDto.North => Direction.North, DirectionDto.East => Direction.East,
        DirectionDto.South => Direction.South, DirectionDto.West => Direction.West,
        _ => throw new ArgumentException("Unknown replay direction.")
    };

    private static ActionFeedback ToFeedback(ActionOutcome outcome) => new()
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
