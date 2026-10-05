using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AgentGame.Core;
using AgentGame.Protocol;

namespace AgentGame.Runtime;

/// <summary>
/// Error surfaced by <see cref="ExternalGameSession"/> to an external controller. <see cref="StatusCode"/>
/// is the intended HTTP-like status and <see cref="Code"/> is a stable machine-readable code used by
/// the controller to dispatch behavior (<c>invalid_action</c>, <c>stale_request</c>, <c>episode_ended</c>,
/// <c>session_failed</c>, <c>record_error</c>, <c>session_closed</c>).
/// </summary>
public class ExternalSessionException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public ExternalSessionException(int statusCode, string code, string detail) : base(detail)
    {
        StatusCode = statusCode;
        Code = code;
    }
}

/// <summary>
/// Point-in-time snapshot of an <see cref="ExternalGameSession"/>. Snake-case JSON names for
/// transport; the session advances only in response to a submitted action, never on a timer.
/// </summary>
public sealed record ExternalSessionStatus
{
    [JsonPropertyName("format")] public string Format { get; init; } = "external-session/1";
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";
    /// <summary>One of <c>waiting</c>, <c>completed</c>, <c>failed</c>, <c>closed</c>.</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("tick")] public long Tick { get; init; }
    /// <summary>The pending request id to echo in the next action while <c>waiting</c>; otherwise null.</summary>
    [JsonPropertyName("request_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? RequestId { get; init; }
    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public EpisodeBody? Result { get; init; }
    /// <summary>Stable error code when the session is <c>failed</c>; otherwise null.</summary>
    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Error { get; init; }
}

/// <summary>
/// An externally controlled single-player <c>scenario/1</c> run. There is no Agent process and no
/// autonomous loop: the session stays <c>waiting</c> until the controller submits exactly one
/// action per observation, and no tick advances without external action.
///
/// <list type="bullet">
/// <item>Scenario is frozen and validated before the first await; the authority header is written
/// before the session becomes ready.</item>
/// <item>Every public operation is serialised on one semaphore, so disposal only ever races the
/// transition it already conceded.</item>
/// <item>Once an action is accepted there is no await until the atomic rule transition, and the
/// authority commit finishes independent of a caller disconnect.</item>
/// <item>Observations expose only the local committed tiles; scenario, map, seed, hash and oracle
/// data are never sent.</item>
/// <item>Only host-to-external messages are returned: an <c>observation</c> JSON, or an
/// <c>episode_end</c> JSON once the rules terminate.</item>
/// </list>
/// </summary>
public sealed class ExternalGameSession : IAsyncDisposable
{
    /// <summary>The Agent name recorded in the replay header/footer for external runs.</summary>
    public const string AgentName = "external";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RunCommitter _committer;
    private readonly Game _game;
    private readonly string _sessionId;

    private string? _cachedObservationJson;
    private bool _terminated;
    private bool _failed;
    private string _failureCode = "";
    private bool _closed;
    private bool _committerDisposed;

    private ExternalGameSession(RunCommitter committer, Game game, string sessionId)
    {
        _committer = committer;
        _game = game;
        _sessionId = sessionId;
    }

    public string SessionId => _sessionId;

    /// <summary>
    /// Freeze and validate <paramref name="scenario"/>, start the authority recording (writing the
    /// replay header before the run becomes ready) and return a <c>waiting</c> session.
    /// </summary>
    /// <exception cref="ProtocolException">The scenario is structurally invalid or its reference_length does not match.</exception>
    /// <exception cref="ExternalSessionException">The authority header could not be recorded (503 record_error).</exception>
    public static async Task<ExternalGameSession> CreateAsync(ScenarioDto scenario,
        RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        // Freeze/validate the caller-owned DTO before the first await, exactly like GameRunner/HumanSession.
        ScenarioDto frozen = ScenarioCodec.Parse(ScenarioCodec.Encode(scenario));
        Scenario owned = ScenarioService.ToCoreScenario(frozen);
        ScenarioValidationResult validation = ScenarioValidator.Validate(owned);
        if (!validation.IsValid) throw new ProtocolException($"Invalid scenario: {validation.Error}.");
        if (frozen.ReferenceLength is not null && frozen.ReferenceLength != validation.Reference!.Actions.Length)
            throw new ProtocolException("Invalid scenario: reference_length_mismatch.");

        Game game = Game.Create(owned);
        var committer = new RunCommitter(frozen, game, options ?? new());
        string sessionId = Guid.NewGuid().ToString("N");
        var session = new ExternalGameSession(committer, game, sessionId);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Authority header is written before the session is ready.
            await committer.StartAsync(AgentName);
            if (cancellationToken.IsCancellationRequested)
            {
                await committer.FinishAsync(AgentName, false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            session.RefreshCachedObservation();
            return session;
        }
        catch (OperationCanceledException)
        {
            await DisposeCommitter(committer);
            throw;
        }
        catch (RecordingException error)
        {
            await DisposeCommitter(committer);
            throw new ExternalSessionException(503, "record_error", "Recording failed: " + error.Message);
        }
        catch
        {
            await DisposeCommitter(committer);
            throw;
        }
    }

    /// <summary>Current session snapshot. Always queryable, including after <see cref="DisposeAsync"/>.</summary>
    public async Task<ExternalSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BuildStatus();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Returns the current committed <c>observation</c> JSON. The observation is cached from the last
    /// committed step, so it never exposes uncommitted Game state. A failed session throws
    /// <c>503 session_failed</c> so it is not actionable.
    /// </summary>
    public async Task<string> ObserveJsonAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed) throw Closed();
            if (_failed) throw Failed();
            if (_cachedObservationJson is null) RefreshCachedObservation();
            return _cachedObservationJson!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Submit one <c>action</c> reply. The message is strictly parsed (shape/enum/direction/UTF-8/
    /// unknown/duplicate fields). On success the rule advances atomically, the authority step is
    /// committed, and the next committed <c>observation</c> JSON is returned — or, once the rules
    /// terminate, the terminal <c>episode_end</c> JSON carrying the last accepted request id.
    /// </summary>
    public async Task<string> SubmitJsonAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (message.Length > ProtocolLimits.MaxLineBytes)
            throw InvalidAction("Action JSON exceeds 64 KiB.");
        // Freeze the caller's buffer before waiting for another submit/recording operation.
        byte[] ownedMessage = message.ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed) throw Closed();
            if (_failed) throw Failed();
            if (_terminated) throw new ExternalSessionException(409, "episode_ended", "The episode has ended.");

            ActionResponse action = ParseStrict(ownedMessage);

            string pending = RequestId(_committer.Committed.Tick);
            if (!string.Equals(action.RequestId, pending, StringComparison.Ordinal))
                throw new ExternalSessionException(409, "stale_request",
                    $"Request id '{action.RequestId}' does not match the pending request '{pending}'.");

            GameAction core;
            try { core = MultiScenarioService.ToCoreAction(action.Action); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { throw InvalidAction(error.Message); }

            // Acceptance is complete: no await between here and the atomic rule transition, so a
            // caller disconnect cannot split acceptance from the committed authority step.
            StepResult step = _game.Step(core);
            string acceptedRequestId = action.RequestId;
            try
            {
                await _committer.StepAsync(_game, step, action.Action, acceptedRequestId);
            }
            catch (RecordingException error)
            {
                MarkFailed("record_error");
                throw new ExternalSessionException(503, "record_error", "Recording failed: " + error.Message);
            }

            // Only after the committed step do we refresh the cached committed observation.
            RefreshCachedObservation();

            if (_game.Episode is not null)
            {
                _terminated = true;
                _cachedObservationJson = EncodeEnd(acceptedRequestId);
                try
                {
                    // Complete replay footer at rule end; read queries stay available afterwards.
                    await _committer.FinishAsync(AgentName, true);
                    // Release the completed recording so a separate verify/replay process can open it
                    // on Windows while the HTTP session remains available for terminal queries.
                    await DisposeCommitterOnceAsync();
                }
                catch (RecordingException error)
                {
                    MarkFailed("record_error");
                    throw new ExternalSessionException(503, "record_error", "Recording failed: " + error.Message);
                }
                return _cachedObservationJson;
            }
            return _cachedObservationJson!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Abort an unfinished replay exactly once (writing the aborted footer), dispose the committer
    /// and its owned writer, then close the session. Idempotent; never publishes after close.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return;
            bool abort = !_terminated && !_failed;
            _closed = true;
            RecordingException? failure = null;
            try
            {
                if (abort) await _committer.FinishAsync(AgentName, false);
            }
            catch (RecordingException error) { failure = error; MarkFailed("record_error"); }
            try { await DisposeCommitterOnceAsync(); }
            catch (RecordingException error) { failure ??= error; MarkFailed("record_error"); }
            if (failure is not null)
                throw new ExternalSessionException(503, "record_error", failure.Message);
        }
        finally { _gate.Release(); }
    }

    private static async ValueTask DisposeCommitter(RunCommitter committer)
    {
        try { await committer.DisposeAsync(); }
        catch (RecordingException) { /* Startup failure: owned writer disposal is best-effort. */ }
    }

    private async ValueTask DisposeCommitterOnceAsync()
    {
        if (_committerDisposed) return;
        _committerDisposed = true;
        await _committer.DisposeAsync();
    }

    private ExternalSessionStatus BuildStatus()
    {
        CoreSnapshot committed = _committer.Committed;
        string state = _closed ? "closed" : _failed ? "failed" : _terminated ? "completed" : "waiting";
        return new()
        {
            SessionId = _sessionId,
            State = state,
            Tick = committed.Tick,
            RequestId = state == "waiting" ? RequestId(committed.Tick) : null,
            Result = _terminated && committed.Episode is not null ? MultiScenarioService.ToEpisode(committed.Episode) : null,
            Error = _failed ? _failureCode : null
        };
    }

    /// <summary>Strict parse of one external reply. Rejects Ready and every transport-level defect as
    /// <c>400 invalid_action</c>.</summary>
    private static ActionResponse ParseStrict(ReadOnlySpan<byte> message)
    {
        AgentResponse response;
        try
        {
            response = AgentResponseParser.Parse(message);
        }
        catch (ProtocolException error)
        {
            throw new ExternalSessionException(400, "invalid_action", error.Message);
        }
        if (response is not ActionResponse action)
            throw InvalidAction("The external session only accepts an action reply, not a ready reply.");
        return action;
    }

    private static ExternalSessionException InvalidAction(string detail) =>
        new(400, "invalid_action", detail);

    private static ExternalSessionException Failed() =>
        new(503, "session_failed", "The session has failed.");

    private static ExternalSessionException Closed() =>
        new(409, "session_closed", "The session has been closed.");

    private void MarkFailed(string code)
    {
        _failed = true;
        _failureCode = code;
    }

    private string RequestId(long tick) => _sessionId + ":r" + tick.ToString(CultureInfo.InvariantCulture);

    /// <summary>Recompute the cached observation JSON from the current (committed) Game state.</summary>
    private void RefreshCachedObservation()
    {
        _cachedObservationJson = ProtocolJson.EncodeLine(new ObservationMessage
        {
            RequestId = RequestId(_game.Tick),
            Tick = _game.Tick,
            Observation = ToDto(_game.Observe())
        });
    }

    private string EncodeEnd(string requestId) =>
        ProtocolJson.EncodeLine(new EpisodeEndMessage
        {
            RequestId = requestId,
            Result = MultiScenarioService.ToEpisode(_game.Episode!),
            Observation = ToDto(_game.Observe())
        });

    /// <summary>Maps a Core observation to the agent/1 observation body (local-only mapper, mirroring GameRunner.ToDto).</summary>
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
            Episode = observation.Episode is null ? null : MultiScenarioService.ToEpisode(observation.Episode)
        };
    }
}
