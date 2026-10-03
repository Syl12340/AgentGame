using System;
using System.Globalization;
using System.IO;

namespace AgentGame.Cli;

/// <summary>
/// Provides a pure, testable state machine and console-key reader for replay playback controls.
/// </summary>
internal sealed class ReplayControls
{
    private readonly TimeSpan _frameInterval;
    private bool _pollingUnavailable;

    /// <summary>
    /// Gets the current playback speed multiplier.
    /// </summary>
    public double Speed { get; private set; }

    /// <summary>
    /// Gets a value indicating whether playback is paused.
    /// </summary>
    public bool Paused { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the user requested to quit.
    /// </summary>
    public bool QuitRequested { get; private set; }

    /// <summary>
    /// Gets the number of pending single steps requested by the user.
    /// </summary>
    public int PendingSteps { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplayControls"/> class.
    /// </summary>
    /// <param name="initialSpeedMultiplier">The initial speed multiplier, clamped between 0.25 and 16.0. Defaults to 1.0.</param>
    /// <param name="frameInterval">The nominal delay between two recorded envelopes at speed 1.0. Defaults to 250 ms.</param>
    public ReplayControls(double initialSpeedMultiplier = 1.0, TimeSpan? frameInterval = null)
    {
        Speed = Math.Clamp(initialSpeedMultiplier, 0.25, 16.0);
        _frameInterval = frameInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>
    /// Returns the delay before the next envelope. This method is pure (no console I/O) 
    /// and does not mutate anything except consuming one <see cref="PendingSteps"/>.
    /// </summary>
    /// <returns>
    /// TimeSpan.Zero when paused, when a pending single step is being consumed, 
    /// or when speed is so high that the delay rounds to zero.
    /// </returns>
    public TimeSpan NextDelay()
    {
        // Bookkeeping: if there's a pending single step, consume exactly one step
        // and return a zero delay to advance immediately.
        if (PendingSteps > 0)
        {
            PendingSteps--;
            return TimeSpan.Zero;
        }

        // Bookkeeping: if paused and no pending steps are left, return a zero delay.
        // The caller relies on the Paused property to pause the loop.
        if (Paused)
        {
            return TimeSpan.Zero;
        }

        // Calculate effective delay based on speed: frameInterval / s
        double ticks = _frameInterval.Ticks / Speed;
        TimeSpan delay = TimeSpan.FromTicks((long)ticks);

        // Clamp the delay to [0, 5s]
        TimeSpan maxDelay = TimeSpan.FromSeconds(5);
        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        if (delay > maxDelay)
        {
            return maxDelay;
        }

        return delay;
    }

    /// <summary>
    /// Applies a key to the control state and returns true when the key was recognised.
    /// </summary>
    /// <param name="key">The console key to handle.</param>
    /// <returns>True if the key was handled; otherwise, false.</returns>
    public bool TryHandleKey(ConsoleKeyInfo key)
    {
        char keyChar = char.ToLowerInvariant(key.KeyChar);

        if (key.Key == ConsoleKey.Spacebar)
        {
            Paused = !Paused;
            return true;
        }

        if (key.Key == ConsoleKey.RightArrow || keyChar == '.' || keyChar == 'n')
        {
            PendingSteps++;
            return true;
        }

        if (key.Key == ConsoleKey.UpArrow || keyChar == '+')
        {
            Speed = Math.Min(16.0, Speed * 2.0);
            return true;
        }

        if (key.Key == ConsoleKey.DownArrow || keyChar == '-')
        {
            Speed = Math.Max(0.25, Speed / 2.0);
            return true;
        }

        if (key.Key == ConsoleKey.Escape || keyChar == 'q')
        {
            QuitRequested = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Performs a non-blocking read of currently available console keys and forwards each key to <see cref="TryHandleKey"/>.
    /// If input is redirected or KeyAvailable is unsupported, this becomes a no-op on subsequent calls.
    /// </summary>
    public void PollInput()
    {
        if (_pollingUnavailable)
        {
            return;
        }

        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                TryHandleKey(key);
            }
        }
        catch (InvalidOperationException)
        {
            _pollingUnavailable = true;
        }
        catch (IOException)
        {
            _pollingUnavailable = true;
        }
    }

    /// <summary>
    /// Returns a one-line, colour-free status text.
    /// </summary>
    /// <returns>A formatted hint string such as "replay 1.0x playing  [space pause  . step  +/- speed  q quit]".</returns>
    public string RenderHint()
    {
        string state = Paused ? "paused" : "playing";
        return $"replay {Speed.ToString("0.0", CultureInfo.InvariantCulture)}x {state}  [space pause  . step  +/- speed  q quit]";
    }
}
