using System.Globalization;
using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime.Agents;

namespace AgentGame.Runtime;

/// <summary>One logical owner; Agent/diagnostic I/O never reads or mutates Game.</summary>
public static class GameRunner
{
    public static async Task<RunResult> RunAsync(ScenarioDto scenario, AgentCommand command,
        AgentTimeouts? timeouts = null, CancellationToken cancellationToken = default, RunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(command);
        timeouts ??= new();
        timeouts.Validate();
        // Freeze caller-owned DTO arrays before the first await or process startup.
        ScenarioDto frozen = ScenarioCodec.Parse(ScenarioCodec.Encode(scenario));
        Scenario owned = ScenarioService.ToCoreScenario(frozen);
        ScenarioValidationResult validation = ScenarioValidator.Validate(owned);
        if (!validation.IsValid) throw new ProtocolException($"Invalid scenario: {validation.Error}.");
        if (frozen.ReferenceLength is not null && frozen.ReferenceLength != validation.Reference!.Actions.Length)
            throw new ProtocolException("Invalid scenario: reference_length_mismatch.");
        Game game = Game.Create(owned);
        var committer = new RunCommitter(frozen, game, options ?? new());
        ProcessAgentSession? session = null;
        string? name = null;
        string? pendingRequestId = null;
        int? processId = null;
        AgentFailure? failure = null;
        string kind = "execution_error";
        ShutdownInfo? shutdown = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = ProcessAgentSession.Start(command, timeouts);
            processId = session.ProcessId;
            name = (await session.HandshakeAsync(cancellationToken)).Name;
            await committer.StartAsync(name);
            while (game.Episode is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await committer.StatusAsync(AgentStatusDto.Waiting);
                string requestId = "r" + game.Tick.ToString(CultureInfo.InvariantCulture);
                var message = new ObservationMessage
                { RequestId = requestId, Tick = game.Tick, Observation = ToDto(game.Observe()) };
                pendingRequestId = requestId;
                ActionResponse response = await session.DecideAsync(message, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // No await between acceptance and the atomic rule transition.
                StepResult step = game.Step(ToCore(response.Action));
                await committer.StepAsync(game, step, response.Action, requestId);
                pendingRequestId = null;
            }
            kind = game.Episode.Kind == EpisodeEndKind.Success ? "success" : "turn_limit";
            await session.EndAsync(new EpisodeEndMessage
            { RequestId = committer.LastRequestId!, Result = ToDto(game.Episode), Observation = ToDto(game.Observe()) }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            kind = "cancelled";
            failure = new("cancelled", pendingRequestId is null ? "run" : "decision",
                "Run cancelled before the next rule transition.", pendingRequestId);
        }
        catch (AgentSessionException error)
        {
            kind = "execution_error";
            failure = error.Failure with { Detail = error.Failure.Detail.Length > 1024 ? error.Failure.Detail[..1024] : error.Failure.Detail };
        }
        catch (RecordingException error)
        {
            kind = "execution_error";
            failure = RecordingFailure(error, pendingRequestId);
        }
        catch (Exception error) when (error is ProtocolException or InvalidOperationException or ArgumentException)
        {
            kind = "execution_error";
            failure = new("runtime_error", "run", error.Message.Length > 1024 ? error.Message[..1024] : error.Message, pendingRequestId);
        }
        finally
        {
            if (session is not null) shutdown = await session.CloseAsync(failure is null);
        }
        if (shutdown?.Error is not null)
        {
            failure ??= shutdown.Error;
            if (kind != "cancelled") kind = "execution_error";
        }
        try { await committer.FinishAsync(name, failure is null); }
        catch (RecordingException error) { kind = "execution_error"; failure = RecordingFailure(error, pendingRequestId); }
        finally
        {
            try { await committer.DisposeAsync(); }
            catch (RecordingException error) { kind = "execution_error"; failure = RecordingFailure(error, pendingRequestId); }
        }
        CoreSnapshot committed = committer.Committed;
        EpisodeOutcome? episode = committed.Episode;
        return new(kind, episode?.Terminated == true, episode?.Truncated == true, committed.Tick,
            name, committer.LastRequestId, committer.LastAction, episode is null ? null : ToDto(episode), failure,
            StateEncoding.Hash(committed), session?.StderrTail ?? "", shutdown?.ExitCode, processId, shutdown?.Forced == true);
    }

    private static AgentFailure RecordingFailure(RecordingException error, string? requestId) =>
        new("record_error", "recording", error.Message.Length > 1024 ? error.Message[..1024] : error.Message, requestId);

    private static GameAction ToCore(ActionRequestDto action) => action.Type switch
    {
        ActionTypeDto.Move => GameAction.Move(ToCore(action.Direction!.Value)),
        ActionTypeDto.Interact => GameAction.Interact(ToCore(action.Direction!.Value)),
        ActionTypeDto.Pickup => GameAction.Pickup(),
        ActionTypeDto.Wait => GameAction.Wait(),
        _ => throw new ArgumentException("Unknown parsed action.")
    };
    private static Direction ToCore(DirectionDto direction) => direction switch
    {
        DirectionDto.North => Direction.North, DirectionDto.East => Direction.East,
        DirectionDto.South => Direction.South, DirectionDto.West => Direction.West,
        _ => throw new ArgumentException("Unknown parsed direction.")
    };
    private static EpisodeBody ToDto(EpisodeOutcome outcome) => new()
    { Kind = outcome.Kind == EpisodeEndKind.Success ? EpisodeKindDto.Success : EpisodeKindDto.TurnLimit };

    private static AgentObservationBody ToDto(AgentObservation observation)
    {
        var inventory = new List<ItemKindDto>();
        if (observation.HasKey) inventory.Add(ItemKindDto.Key);
        if (observation.HasCore) inventory.Add(ItemKindDto.Core);
        return new()
        {
            Position = new() { X = observation.Position.X, Y = observation.Position.Y },
            Tiles = observation.Tiles.Select(tile => new AgentTile
            {
                X = tile.Position.X, Y = tile.Position.Y,
                Terrain = tile.Terrain == Terrain.Wall ? TerrainDto.Wall : TerrainDto.Floor,
                Item = tile.Item switch { ItemKind.Key => ItemKindDto.Key, ItemKind.Core => ItemKindDto.Core, _ => null },
                IsExit = tile.IsExit, DoorOpen = tile.DoorOpen
            }).ToArray(),
            Inventory = inventory.ToArray(),
            Mission = observation.Mission switch
            {
                MissionPhase.FindKey => MissionPhaseDto.FindKey, MissionPhase.OpenDoor => MissionPhaseDto.OpenDoor,
                MissionPhase.FindCore => MissionPhaseDto.FindCore, MissionPhase.ReturnToExit => MissionPhaseDto.ReturnToExit,
                MissionPhase.Succeeded => MissionPhaseDto.Succeeded,
                _ => throw new InvalidOperationException("Unknown Core mission.")
            },
            LastResult = observation.LastResult is null ? null : new ActionFeedback
            {
                Status = observation.LastResult.Status switch
                { ActionStatus.Applied => ActionStatusDto.Applied, ActionStatus.Blocked => ActionStatusDto.Blocked, _ => ActionStatusDto.NoEffect },
                Reason = observation.LastResult.Reason
            },
            Episode = observation.Episode is null ? null : ToDto(observation.Episode)
        };
    }
}
