using System.Globalization;
using AgentGame.Protocol;

namespace AgentGame.Cli;

/// <summary>
/// Reads one action per step from the console for a human-driven run. The reader only touches the
/// terminal through <see cref="Console.ReadKey(bool)"/> and the <see cref="TextWriter"/> it is
/// handed: it never writes to <see cref="Console.Out"/>, so a protocol stream on stdout stays intact
/// and prompts belong to the caller (normally stderr).
/// </summary>
internal sealed class HumanInput
{
    /// <summary>The key legend shown on every prompt.</summary>
    internal const string PromptLine =
        "[arrows/WASD] move   [E then direction, or Shift+arrow] interact   [space/P] pickup   [.] wait   [Q/Esc] quit";

    /// <summary>How many rejected keys in a row still get a fresh prompt before falling back to wait.</summary>
    private const int MaxInvalidKeys = 16;

    /// <summary>The no-op action used whenever the prompt cannot produce a real decision.</summary>
    private static ActionRequestDto Wait => new() { Type = ActionTypeDto.Wait };

    private bool _interactPending;
    private bool _terminalUnreadable;
    private int _invalidKeys;

    /// <summary>
    /// Reads one step for <paramref name="observation"/>, re-prompting on unrecognized keys.
    /// Null is reserved for "the human asked to quit" (Q or Esc): an unusable terminal, or
    /// <see cref="MaxInvalidKeys"/> rejected keys in a row, falls back to <c>wait</c> instead of
    /// ending the run, so a broken prompt can never be mistaken for user intent.
    /// </summary>
    /// <param name="observation">The observation being decided on; its step line is shown in the prompt.</param>
    /// <param name="prompt">Receives all human-facing text. Never a protocol stream.</param>
    public ActionRequestDto? ReadStep(AgentObservationBody observation, TextWriter prompt)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(prompt);
        while (true)
        {
            if (_invalidKeys >= MaxInvalidKeys)
            {
                // Too many rejected keys: consume no turn rather than stalling the prompt forever.
                _invalidKeys = 0;
                _interactPending = false;
                return Wait;
            }
            ActionRequestDto? step = ReadStepOrNull(observation, prompt);
            if (step is not null) return step;
            // A pending 'e' is a mode toggle, not a quit, so it re-prompts instead of ending the run.
            if (_interactPending) continue;
            // An unreadable terminal is also not a quit: never spend a turn on it, never end the run.
            if (_terminalUnreadable)
            {
                _terminalUnreadable = false;
                return Wait;
            }
            return null;
        }
    }

    /// <summary>
    /// One prompt attempt: writes the prompt and reads exactly one key. Returns the parsed action, or
    /// null when the key was invalid, was a quit key, or could not be read. Terminal I/O problems are
    /// reported as null rather than thrown.
    /// </summary>
    /// <param name="observation">Current observation; its step line is shown above the key legend.</param>
    /// <param name="prompt">Human-facing output target.</param>
    public ActionRequestDto? ReadStepOrNull(AgentObservationBody observation, TextWriter prompt)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(prompt);
        _terminalUnreadable = false;
        bool restoreCursor = TryHideCursor();
        try
        {
            WritePrompt(prompt, observation);
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(true); }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                // Redirected or closed input: there is no human answer to collect for this step.
                _terminalUnreadable = true;
                return null;
            }
            ActionRequestDto? action = Resolve(key);
            if (action is null) _invalidKeys++;
            else _invalidKeys = 0;
            return action;
        }
        catch (IOException)
        {
            // The prompt target itself failed; the terminal state is just as unusable.
            _terminalUnreadable = true;
            return null;
        }
        finally
        {
            if (restoreCursor) RestoreCursor();
        }
    }

    /// <summary>
    /// Maps one key to an action without any I/O. A null result means "invalid key" or "quit"; the
    /// caller distinguishes them by <see cref="_interactPending"/> and by whether the key was a quit key.
    /// </summary>
    private ActionRequestDto? Resolve(ConsoleKeyInfo key)
    {
        // Quit keys are always available; they just disarm a pending interact first.
        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            _interactPending = false;
            return null;
        }
        bool interact = _interactPending || key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if (TryDirection(key, out DirectionDto direction))
        {
            _interactPending = false;
            return interact
                ? new ActionRequestDto { Type = ActionTypeDto.Interact, Direction = direction }
                : new ActionRequestDto { Type = ActionTypeDto.Move, Direction = direction };
        }
        // A direction was expected (Shift held or 'e' armed) but the key is not a direction: reject.
        if (interact)
        {
            _interactPending = false;
            return null;
        }
        if (IsKey(key, ConsoleKey.E, 'e'))
        {
            _interactPending = true;
            return null;
        }
        if (key.Key == ConsoleKey.Spacebar || IsKey(key, ConsoleKey.P, 'p'))
            return new ActionRequestDto { Type = ActionTypeDto.Pickup };
        if (IsKey(key, ConsoleKey.OemPeriod, '.'))
            return new ActionRequestDto { Type = ActionTypeDto.Wait };
        return null;
    }

    /// <summary>Absolute cardinal direction for arrows and WASD; false for any other key.</summary>
    private static bool TryDirection(ConsoleKeyInfo key, out DirectionDto direction)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: direction = DirectionDto.North; return true;
            case ConsoleKey.RightArrow: direction = DirectionDto.East; return true;
            case ConsoleKey.DownArrow: direction = DirectionDto.South; return true;
            case ConsoleKey.LeftArrow: direction = DirectionDto.West; return true;
        }
        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'w': direction = DirectionDto.North; return true;
            case 'd': direction = DirectionDto.East; return true;
            case 's': direction = DirectionDto.South; return true;
            case 'a': direction = DirectionDto.West; return true;
        }
        direction = default;
        return false;
    }

    /// <summary>Matches a key by virtual key or by character, so layout-independent input also works.</summary>
    private static bool IsKey(ConsoleKeyInfo key, ConsoleKey expected, char expectedChar) =>
        key.Key == expected || char.ToLowerInvariant(key.KeyChar) == expectedChar;

    private void WritePrompt(TextWriter prompt, AgentObservationBody observation)
    {
        string episode = observation.Episode is null
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $" episode={observation.Episode.Kind}");
        prompt.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"at {observation.Position.X},{observation.Position.Y} mission={observation.Mission}{episode}"));
        prompt.WriteLine(_interactPending ? PromptLine + "   (interact armed: press a direction)" : PromptLine);
        prompt.Flush();
    }

    /// <summary>
    /// Hides the cursor only while a key is awaited. Returns true when the caller must restore it;
    /// unsupported hosts (no console, redirected output) keep the cursor as it is.
    /// </summary>
    private static bool TryHideCursor()
    {
        try
        {
            // CursorVisible is only implemented on Windows; elsewhere the prompt is left alone.
            if (OperatingSystem.IsWindows() && Console.CursorVisible) Console.CursorVisible = false;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Restores the cursor even on the exceptional paths; a failed restore is not fatal.</summary>
    private static void RestoreCursor()
    {
        try { if (OperatingSystem.IsWindows()) Console.CursorVisible = true; }
        catch (Exception error) when (error is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            // The terminal refused the change; there is nothing further to restore.
        }
    }
}
