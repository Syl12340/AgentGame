using System.Globalization;
using AgentGame.Core;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;
using AgentGame.Runtime.Agents;

namespace AgentGame.Runtime;

/// <summary>One logical rule owner; each failed seat is permanently retired to recorded wait.</summary>
public static class MultiGameRunner
{
    public static async Task<MultiRunResult> RunAsync(MultiScenarioDto scenario, IReadOnlyList<IMultiSeatSource> seats,
        AgentTimeouts? timeouts = null, CancellationToken cancellationToken = default, MultiRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(seats);
        timeouts ??= new();
        timeouts.Validate();
        options ??= new();
        MultiScenarioDto frozen = MultiScenarioCodec.Parse(MultiScenarioCodec.Encode(scenario));
        IMultiSeatSource[] sources = seats.ToArray();
        if (sources.Length != 2 || frozen.Spawns.Length != 2 || sources.Any(s => s is null) ||
            sources.Distinct(ReferenceEqualityComparer.Instance).Count() != sources.Length)
            throw new ArgumentException("This runtime requires two distinct, non-null seat sources and two spawns.");
        if (options.RecordPath is not null && options.ReplayWriter is not null)
            throw new ArgumentException("Specify RecordPath or ReplayWriter, not both.");
        var view = M2Protocol.ParseView(options.RecordView);
        if (!view.IsSpectator && view.Seat >= sources.Length) throw new ArgumentException("Record view seat does not exist.");
        MultiScenario owned = MultiScenarioService.ToCoreScenario(frozen);
        var validation = MultiScenarioValidator.Validate(owned);
        if (!validation.IsValid) throw new ProtocolException("Invalid scenario: " + validation.Error);
        if (frozen.ReferenceLength is not null && frozen.ReferenceLength != validation.Reference!.Actions.Length)
            throw new ProtocolException("Invalid scenario: reference_length_mismatch.");

        MultiGame game = MultiGame.Create(owned);
        var states = sources.Select((source, seat) => new SeatState(source, seat)).ToArray();
        MultiRunCommitter? committer = null;
        AgentFailure? failure = null;
        string kind = "execution_error";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            committer = new(frozen, game, options);
            await committer.StartAsync("multiplayer");
            foreach (var state in states)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { state.Name = await CallAsync(token => state.Source.StartAsync(state.Seat, states.Length, token),
                    timeouts.Handshake, "handshake", null); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                { await RetireAsync(state, SourceFailure(error, "handshake", null)); }
                if (failure is not null) throw new InvalidOperationException("Seat cleanup failed.");
            }
            while (game.Episode is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await committer.StatusAsync(AgentStatusDto.Waiting);
                var state = states[game.OwnerSeat];
                string requestId = "r" + game.Tick.ToString(CultureInfo.InvariantCulture);
                state.LastRequestId = requestId;
                ActionRequestDto action = new() { Type = ActionTypeDto.Wait };
                GameAction coreAction = GameAction.Wait();
                if (!state.Retired)
                {
                    var message = new AgentV2ObservationMessage { RequestId = requestId, Tick = game.Tick,
                        Seat = state.Seat, Observation = MultiScenarioService.ToObservation(game.Observe(state.Seat)) };
                    try
                    {
                        ActionRequestDto response = await CallAsync(token => state.Source.DecideAsync(message, token),
                            timeouts.Decision, "decision", requestId);
                        ArgumentNullException.ThrowIfNull(response);
                        action = response with { };
                        coreAction = MultiScenarioService.ToCoreAction(action);
                    }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                    {
                        await RetireAsync(state, SourceFailure(error, "decision", requestId));
                        action = new() { Type = ActionTypeDto.Wait };
                        coreAction = GameAction.Wait();
                    }
                }
                if (failure is not null) throw new InvalidOperationException("Seat cleanup failed.");
                cancellationToken.ThrowIfCancellationRequested();
                // No asynchronous work between acceptance and the single-owner rule transition.
                MultiStepResult step = game.Step(state.Seat, coreAction);
                await committer.StepAsync(game, step, action);
            }
            kind = game.Episode.Kind == EpisodeEndKind.Success ? "success" : "turn_limit";
            foreach (var state in states.Where(s => !s.Retired))
            {
                try
                {
                    var message = new AgentV2EpisodeEndMessage { RequestId = state.LastRequestId ?? "end",
                        Result = MultiScenarioService.ToEpisode(game.Episode),
                        Observation = MultiScenarioService.ToObservation(game.Observe(state.Seat)) };
                    await CallAsync(async token => { await state.Source.EndAsync(message, token); return true; },
                        timeouts.Decision, "episode_end", state.LastRequestId);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                { await RetireAsync(state, SourceFailure(error, "episode_end", state.LastRequestId)); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            kind = "cancelled";
            failure ??= new("cancelled", "run", "Run cancelled before the next rule transition.");
        }
        catch (RecordingException error) { failure ??= RecordingFailure(error); kind = "execution_error"; }
        catch (Exception error)
        {
            failure ??= new(error is IOException or UnauthorizedAccessException ? "record_error" : "execution_failed",
                "run", Detail(error));
            kind = "execution_error";
        }
        finally
        {
            foreach (var state in states) await CloseAsync(state, failure is null && game.Episode is not null && !state.Retired);
        }
        if (failure is not null && kind != "cancelled") kind = "execution_error";
        if (states.Any(s => s.Cleanup?.Error is not null)) kind = "execution_error";
        if (committer is not null)
        {
            try { await committer.FinishAsync("multiplayer", failure is null && committer.Committed.Episode is not null,
                new { seats = Results() }); }
            catch (Exception error) { failure ??= RecordingFailure(error); kind = "execution_error"; }
            finally
            {
                try { await committer.DisposeAsync(); }
                catch (Exception error) { failure ??= RecordingFailure(error); kind = "execution_error"; }
            }
        }
        else if (options.ReplayWriter is not null)
        {
            // Ownership includes an injected writer when cancellation precedes committer creation.
            try { await options.ReplayWriter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { failure ??= RecordingFailure(error); kind = "execution_error"; }
        }
        MultiSnapshot committed = committer?.Committed ?? game.Capture();
        return new(kind, committed.Episode?.Terminated == true, committed.Episode?.Truncated == true,
            committed.Tick, MultiStateEncoding.Hash(committed),
            committed.Episode is null ? null : MultiScenarioService.ToEpisode(committed.Episode), failure, Results());

        MultiSeatResult[] Results() => states.Select(s => new MultiSeatResult(s.Seat, s.Name, s.Retired, s.Failure, s.Cleanup)).ToArray();

        async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, string phase, string? requestId)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            Task<T> work = call(deadline.Token);
            ArgumentNullException.ThrowIfNull(work);
            try { return await work.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                Observe(work);
                throw new AgentSessionException(phase == "handshake" ? "handshake_timeout" : "decision_timeout", phase,
                    "Seat response deadline exceeded.", requestId);
            }
            catch { Observe(work); throw; }
        }

        async Task RetireAsync(SeatState state, AgentFailure error)
        {
            state.Retired = true;
            state.Failure ??= error;
            await CloseAsync(state, false);
        }

        async Task CloseAsync(SeatState state, bool graceful)
        {
            if (state.Closed) return;
            state.Closed = true;
            try
            {
                var work = state.Source.CloseAsync(graceful);
                try { state.Cleanup = await work.WaitAsync(timeouts.ShutdownGrace + timeouts.KillWait); }
                catch { if (work is not null) Observe(work); throw; }
                if (state.Cleanup is null) throw new InvalidOperationException("Seat cleanup returned no result.");
            }
            catch (Exception error)
            { state.Cleanup = new(null, null, false, new("cleanup_failed", "shutdown", Detail(error)), ""); }
            if (state.Cleanup.Error is { } cleanupError)
            { state.Failure ??= cleanupError; failure ??= cleanupError; }
        }
    }

    private sealed class SeatState(IMultiSeatSource source, int seat)
    {
        internal IMultiSeatSource Source { get; } = source;
        internal int Seat { get; } = seat;
        internal string? Name, LastRequestId;
        internal bool Retired, Closed;
        internal AgentFailure? Failure;
        internal MultiSeatCleanup? Cleanup;
    }

    private static AgentFailure SourceFailure(Exception error, string phase, string? requestId) => error switch
    {
        AgentSessionException session => session.Failure,
        OperationCanceledException => new("seat_disconnected", phase, Detail(error), requestId),
        ProtocolException or ArgumentException => new("protocol_violation", phase, Detail(error), requestId),
        _ => new(phase == "handshake" ? "start_failed" : "decision_failed", phase, Detail(error), requestId)
    };
    private static AgentFailure RecordingFailure(Exception error) => new("record_error", "recording", Detail(error));
    private static string Detail(Exception error) => error.Message.Length > 1024 ? error.Message[..1024] : error.Message;
    private static void Observe(Task work) => _ = work.ContinueWith(t => { _ = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
