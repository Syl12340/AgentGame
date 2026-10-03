using System.Globalization;
using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime.Agents;

namespace AgentGame.Runtime;

/// <summary>
/// One logical owner for a human-driven run. The rule loop, the commit path and the result
/// shape are the same as <see cref="GameRunner.RunAsync"/>; only the decision source differs:
/// the next action arrives from an in-process callback instead of an Agent process.
/// This type deliberately does not reference Process, ProcessAgentSession or any other
/// Agent-process type, so a terminal front end can own it without process semantics.
/// </summary>
public static class HumanSession
{
    /// <summary>The Agent name recorded for every human run in the replay header and footer.</summary>
    public const string AgentName = "human";

    /// <summary>Maximum accepted decision detail length, matching GameRunner's truncation.</summary>
    private const int MaxDetailLength = 1024;

    /// <summary>
    /// Plays one scenario to its rule end (or failure) by asking <paramref name="decide"/> for each step.
    /// </summary>
    /// <param name="scenario">Caller-owned scenario DTO; frozen before the first await.</param>
    /// <param name="decide">
    /// Produces the next action for one observation. The observation DTO is owned by this run and must
    /// not be mutated or retained beyond the call. Returning an action that is not a legal
    /// move/interact/pickup/wait (for example interact without a direction) fails the run without
    /// consuming a tick. Throwing <see cref="OperationCanceledException"/> cancels the run.
    /// </param>
    /// <param name="timeouts">
    /// Accepted for signature parity with <see cref="GameRunner.RunAsync"/>. A human session has no
    /// external process, so no handshake, decision or shutdown deadline is armed; only the same
    /// structural validation is applied so bad caller values still fail before anything runs.
    /// </param>
    /// <param name="cancellationToken">Cancels the run at a request boundary; no partial step is committed.</param>
    /// <param name="options">Recording/observer options; the run owns and disposes any injected writer.</param>
    /// <returns>
    /// A <see cref="RunResult"/> with the same field semantics as a process run, except for the
    /// process-only fields:
    /// <list type="bullet">
    /// <item><c>AgentProcessId</c> = 0 (there is no external process; 0 means "none").</item>
    /// <item><c>AgentExitCode</c> = 0 on a normal rule end, 130 when cancelled, 1 on execution error —
    /// the logical session exit code, matching the CLI's exit-code mapping; it is not an OS status.</item>
    /// <item><c>ForcedTermination</c> = false (nothing was ever killed).</item>
    /// <item><c>StderrTail</c> = "" (no child stderr is drained).</item>
    /// <item><c>AgentName</c> = <see cref="AgentName"/> ("human").</item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="scenario"/> or <paramref name="decide"/> is null.</exception>
    /// <exception cref="ProtocolException">The scenario is structurally invalid or its reference_length does not match.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeouts"/> contains a value GameRunner would reject.</exception>
    public static async Task<RunResult> PlayAsync(ScenarioDto scenario,
        Func<AgentObservationBody, CancellationToken, Task<ActionRequestDto>> decide,
        AgentTimeouts? timeouts = null, CancellationToken cancellationToken = default, RunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(decide);
        timeouts ??= new();
        timeouts.Validate();
        // Freeze caller-owned DTO arrays before any await, exactly as GameRunner does.
        ScenarioDto frozen = ScenarioCodec.Parse(ScenarioCodec.Encode(scenario));
        Scenario owned = ScenarioService.ToCoreScenario(frozen);
        ScenarioValidationResult validation = ScenarioValidator.Validate(owned);
        if (!validation.IsValid) throw new ProtocolException($"Invalid scenario: {validation.Error}.");
        if (frozen.ReferenceLength is not null && frozen.ReferenceLength != validation.Reference!.Actions.Length)
            throw new ProtocolException("Invalid scenario: reference_length_mismatch.");
        Game game = Game.Create(owned);
        var committer = new RunCommitter(frozen, game, options ?? new());
        string? pendingRequestId = null;
        AgentFailure? failure = null;
        string kind = "execution_error";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await committer.StartAsync(AgentName);
            while (game.Episode is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await committer.StatusAsync(AgentStatusDto.Waiting);
                string requestId = "r" + game.Tick.ToString(CultureInfo.InvariantCulture);
                var observation = ToDto(game.Observe());
                pendingRequestId = requestId;
                ActionRequestDto response = await decide(observation, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Accept (validate) first: an illegal action must not reach the rule transition...
                GameAction action = ToCoreAction(response);
                // ...and there must be no await between acceptance and the atomic rule transition.
                StepResult step = game.Step(action);
                // Records the committed step; the request id is cleared only after the commit point.
                await committer.StepAsync(game, step, response, requestId);
                pendingRequestId = null;
            }
            kind = game.Episode.Kind == EpisodeEndKind.Success ? "success" : "turn_limit";
        }
        catch (OperationCanceledException)
        {
            kind = "cancelled";
            failure = new("cancelled", pendingRequestId is null ? "run" : "decision",
                "Run cancelled before the next rule transition.", pendingRequestId);
        }
        catch (RecordingException error)
        {
            kind = "execution_error";
            failure = RecordingFailure(error, pendingRequestId);
        }
        catch (Exception error) when (error is ProtocolException or InvalidOperationException or ArgumentException)
        {
            kind = "execution_error";
            failure = new("decision_failed", pendingRequestId is null ? "run" : "decision",
                Truncate(error.Message), pendingRequestId);
        }
        try { await committer.FinishAsync(AgentName, failure is null); }
        catch (RecordingException error) { kind = "execution_error"; failure = RecordingFailure(error, pendingRequestId); }
        finally
        {
            try { await committer.DisposeAsync(); }
            catch (RecordingException error) { kind = "execution_error"; failure = RecordingFailure(error, pendingRequestId); }
        }
        CoreSnapshot committed = committer.Committed;
        EpisodeOutcome? episode = committed.Episode;
        return new(kind, episode?.Terminated == true, episode?.Truncated == true, committed.Tick,
            AgentName, committer.LastRequestId, committer.LastAction, episode is null ? null : ToDto(episode), failure,
            StateEncoding.Hash(committed), "", ExitCodeFor(kind), 0, false);
    }

    /// <summary>Logical session exit code: the CLI's mapping, not an OS process status.</summary>
    private static int ExitCodeFor(string kind) => kind switch
    {
        "cancelled" => 130,
        "execution_error" => 1,
        _ => 0
    };

    private static AgentFailure RecordingFailure(RecordingException error, string? requestId) =>
        new("record_error", "recording", Truncate(error.Message), requestId);

    private static string Truncate(string detail) =>
        detail.Length > MaxDetailLength ? detail[..MaxDetailLength] : detail;

    /// <summary>
    /// Maps one decision to an immutable Core action and rejects anything GameRunner would not
    /// have parsed off the wire. Throwing here happens before <see cref="Game.Step"/>, so the tick
    /// and the committed snapshot stay untouched.
    /// </summary>
    private static GameAction ToCoreAction(ActionRequestDto action)
    {
        ArgumentNullException.ThrowIfNull(action);
        bool directional = action.Type is ActionTypeDto.Move or ActionTypeDto.Interact;
        if (directional != action.Direction.HasValue || (action.Direction.HasValue && !Enum.IsDefined(action.Direction.Value)))
            throw new ArgumentException("A decision must carry a direction exactly for move/interact and must use a defined direction.");
        return action.Type switch
        {
            ActionTypeDto.Move => GameAction.Move(ToCore(action.Direction!.Value)),
            ActionTypeDto.Interact => GameAction.Interact(ToCore(action.Direction!.Value)),
            ActionTypeDto.Pickup => GameAction.Pickup(),
            ActionTypeDto.Wait => GameAction.Wait(),
            _ => throw new ArgumentException("Unknown action type.")
        };
    }

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
