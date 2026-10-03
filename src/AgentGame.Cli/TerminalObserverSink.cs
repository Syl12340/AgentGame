#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentGame.Runtime.Observer;

namespace AgentGame.Cli;

/// <summary>
/// Renders a live Observer subscription to an interactive terminal. The sink owns one
/// <see cref="TerminalView"/> plus the interactive <see cref="ReplayControls"/> so the player
/// can pause, single-step, change speed and quit without touching the rule owner: it never
/// calls Core, never blocks the run loop, and only ever consumes the bounded subscription.
/// </summary>
internal sealed class TerminalObserverSink : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly ObserverRegistration _registration;
    private readonly Stream _output;
    private readonly ReplayControls _controls;
    private readonly TerminalView _view;
    private readonly bool _interactiveControls;
    private readonly CancellationTokenSource _cts = new();

    public Task Completion { get; }
    public string? Error { get; private set; }

    /// <param name="interactiveControls">
    /// True only when this sink is the sole owner of console input (``run --tui``). Human play
    /// must pass false: there the HumanInput prompt reads the same console for real moves, and
    /// two readers racing for keys would steal moves and quit the view by accident.
    /// </param>
    public TerminalObserverSink(ObserverRegistration registration, Stream output, ReplayControls? controls = null,
        bool useColor = true, Func<TimeSpan>? clock = null, bool interactiveControls = true)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(output);
        _registration = registration;
        _output = output;
        _controls = controls ?? new ReplayControls();
        _interactiveControls = interactiveControls;
        _view = new TerminalView(output, SafeConsoleSize(Console.WindowWidth, 80), SafeConsoleSize(Console.WindowHeight, 24), useColor, clock);
        Completion = Task.Run(ConsumeAsync);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            _view.RenderSnapshot(_registration.Snapshot);
            _view.Draw();
            if (_interactiveControls) WriteHint();

            await foreach (object envelope in _registration.Subscription.Reader.ReadAllAsync(_cts.Token))
            {
                if (_cts.IsCancellationRequested) break;
                if (_interactiveControls)
                {
                    _controls.PollInput();
                    if (_controls.QuitRequested) break;
                }
                if (!_view.TryApply(envelope))
                {
                    // The view keeps the last trustworthy frame; a gap means we must re-snapshot
                    // instead of rendering a world built from non-contiguous patches.
                    if (_view.NeedsResync) { Error = "resync_required"; break; }
                    continue;
                }
                if (_registration.Subscription.RequiresResync) { Error = "resync_required"; break; }

                TimeSpan delay = _interactiveControls ? _controls.NextDelay() : TimeSpan.Zero;
                if (_interactiveControls)
                {
                    while (_controls.Paused && _controls.PendingSteps == 0 && !_controls.QuitRequested)
                    {
                        _controls.PollInput();
                        await Task.Delay(50, _cts.Token);
                    }
                    if (_controls.QuitRequested) break;
                }
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _cts.Token);
                _view.Draw();
                if (_interactiveControls) WriteHint();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Clean shutdown: the owner disposed the sink.
        }
        catch (Exception)
        {
            Error ??= "output_error";
        }
        finally
        {
            if (Error is null && _registration.Subscription.RequiresResync) Error = "resync_required";
            _registration.Subscription.Dispose();
        }
    }

    private void WriteHint()
    {
        byte[] bytes = Utf8.GetBytes(_controls.RenderHint() + "\n");
        _output.Write(bytes);
        _output.Flush();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _registration.Subscription.Dispose();
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        catch (Exception) { }
        try
        {
            byte[] restore = Utf8.GetBytes(_view.RenderRestoreSequence());
            if (restore.Length > 0) { _output.Write(restore); _output.Flush(); }
        }
        catch (Exception) { }
        _view.Dispose();
        _cts.Dispose();
    }

    private static int SafeConsoleSize(int value, int fallback)
    {
        try { return value > 0 ? value : fallback; }
        catch (Exception) { return fallback; }
    }
}
