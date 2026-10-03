#nullable enable
using System.Globalization;
using System.Text;
using AgentGame.Protocol;
using AgentGame.Runtime.Observer;

namespace AgentGame.Cli;

/// <summary>
/// Pure terminal renderer for the observer/1 stream (agent view). It owns no game logic: the logical
/// state comes from the existing <see cref="VisualState"/> reducer (explicit wire patches only, never
/// a re-derivation of the rules), and this type only turns that state into text.
/// <para>Invariants kept here:</para>
/// <list type="bullet">
/// <item><b>Sequence continuity.</b> An envelope is applied only when its seq is exactly the cached
/// BaseSeq + 1. A gap, a duplicate, a stream identity change or a rejected patch never mutates the
/// display state: the view raises <see cref="NeedsResync"/> and then refuses every further envelope
/// until a fresh snapshot arrives, so disjoint patches can never be blended into a wrong world. The
/// last trusted baseline stays renderable while the caller resynchronises.</item>
/// <item><b>Display memory is not current truth.</b> <c>last_seen_tick == null</c> is the only
/// authoritative "currently visible" signal. A non-null value means the cell is only remembered: it
/// is drawn dimmed and with its own memory character ('.'->',', '#'->'%', 'k'->'K', 'C'->'c',
/// '+'->'=', '/'->'\', '>'->'v'), so even a no-colour sink can never mistake memory for current
/// terrain. A cell with no record at all is unknown ('?') and is never drawn as floor or wall.</item>
/// <item><b>No side channels.</b> This type writes nothing to stdout/stderr/Console by itself and
/// reads no file, environment variable or credential. <see cref="RenderFrame"/> is pure: it only
/// reads the state and caches produced by the last accepted envelope.</item>
/// <item><b>ANSI only when asked.</b> With <see cref="UseColor"/> disabled the renderer emits no ESC
/// byte at all (plain text for redirection and tests); cursor positioning and per-line erase exist
/// only in colour mode.</item>
/// </list>
/// </summary>
internal sealed class TerminalView : IDisposable
{
    /// <summary>Largest observer/1 safe integer; a baseline at this seq has no representable successor.</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const int DefaultWidth = 80;
    private const int DefaultHeight = 24;
    private const int MaxWidth = 4096;
    private const int MaxHeight = 512;

    /// <summary>Below this width the view degrades to a status summary plus a small window.</summary>
    private const int CompactWidthThreshold = 40;

    /// <summary>Below this width even the compact map window is dropped (summary only).</summary>
    private const int MapMinWidth = 20;

    /// <summary>Upper bound on retained semantic events; only the newest ones that fit are drawn.</summary>
    private const int MaxEvents = 16;

    // ANSI sequences. Emitted only when _useColor is true, so a redirected sink stays pure text.
    private const string AnsiReset = "\u001b[0m";
    private const string AnsiHome = "\u001b[H";
    private const string AnsiClearLine = "\u001b[K";
    private const string AnsiClearBelow = "\u001b[J";
    private const string AnsiHideCursor = "\u001b[?25l";
    private const string AnsiShowCursor = "\u001b[?25h";
    private const string ColorHeader = "\u001b[1;36m";
    private const string ColorAlert = "\u001b[1;31m";
    private const string ColorLabel = "\u001b[36m";
    private const string ColorFeedback = "\u001b[33m";
    private const string ColorFloor = "\u001b[37m";
    private const string ColorWall = "\u001b[1;37m";
    private const string ColorPlayer = "\u001b[1;93m";
    private const string ColorKey = "\u001b[96m";
    private const string ColorCore = "\u001b[95m";
    private const string ColorExit = "\u001b[92m";
    private const string ColorDoorOpen = "\u001b[94m";
    private const string ColorDoorClosed = "\u001b[93m";

    /// <summary>Dim grey: display memory, unknown cells, secondary text.</summary>
    private const string ColorDim = "\u001b[90m";

    // ---- configuration and sink ----
    private readonly bool _useColor;
    private readonly Func<TimeSpan>? _clock;
    private TextWriter? _writer;
    private bool _ownsWriter;
    private int _width = DefaultWidth;
    private int _height = DefaultHeight;

    // ---- display state: one owned deep copy of the reducer state, refreshed per accepted envelope ----
    private VisualState? _reducer;
    private SnapshotMessage? _state;
    private readonly Dictionary<(int X, int Y), ObserverTile> _tiles = [];
    private readonly List<string> _events = [];
    private string _lastFeedback = "none";
    private int _minX;
    private int _minY;
    private int _maxX;
    private int _maxY;
    private bool _hasBounds;
    private int _playerX;
    private int _playerY;
    private bool _hasPlayer;

    // ---- animation / timing cache (cleared by Reset) ----
    private TimeSpan _waitingSince;
    private bool _waitingSinceValid;

    // ---- stream health ----
    private bool _needsResync = true; // no baseline yet: nothing can be applied before a snapshot
    private string? _rejectReason;

    /// <summary>Creates a view that draws into <paramref name="output"/> (UTF-8, no BOM, stream left open).</summary>
    public TerminalView(Stream output, int? width = null, int? height = null, bool useColor = true, Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true);
        _ownsWriter = true;
        _useColor = useColor;
        _clock = clock;
        Width = width ?? DefaultWidth;
        Height = height ?? DefaultHeight;
    }

    /// <summary>Creates a view over a caller-owned writer. The writer is flushed but never disposed.</summary>
    public TerminalView(TextWriter output, int? width = null, int? height = null, bool useColor = true, Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _writer = output;
        _ownsWriter = false;
        _useColor = useColor;
        _clock = clock;
        Width = width ?? DefaultWidth;
        Height = height ?? DefaultHeight;
    }

    /// <summary>Layout width in columns; clamped to [1, 4096] so a requested width is never exceeded.</summary>
    public int Width
    {
        get => _width;
        set => _width = Math.Clamp(value, 1, MaxWidth);
    }

    /// <summary>Layout height in rows; clamped to [1, 512].</summary>
    public int Height
    {
        get => _height;
        set => _height = Math.Clamp(value, 1, MaxHeight);
    }

    /// <summary>False means every frame is plain text and contains no ESC byte.</summary>
    public bool UseColor => _useColor;

    /// <summary>True once a snapshot has established a renderable baseline.</summary>
    public bool HasSnapshot => _state is not null;

    /// <summary>True when the ordered stream cannot be trusted any more and a fresh snapshot is required.</summary>
    public bool NeedsResync => _needsResync;

    /// <summary>Reason for the last refused envelope, for the caller to route to diagnostics if it wants.</summary>
    public string? LastRejectReason => _rejectReason;

    /// <summary>
    /// Establishes a new display baseline from a full observer/1 snapshot: display memory, the event
    /// log, the last feedback and the animation/timing caches are replaced and the view becomes
    /// trustworthy again. The snapshot is validated and copied by the reducer, so the view never
    /// aliases caller-owned arrays. Throws <see cref="ProtocolException"/> when the snapshot violates
    /// observer/1 invariants; the previous baseline is then left untouched.
    /// </summary>
    public void RenderSnapshot(SnapshotMessage snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        VisualState reducer = VisualState.FromSnapshot(snapshot);
        SnapshotMessage state = reducer.Snapshot();
        _reducer = reducer;
        _state = state;
        _events.Clear();
        _lastFeedback = "none";
        _rejectReason = null;
        _needsResync = false;
        _waitingSince = TimeSpan.Zero;
        _waitingSinceValid = false;
        RefreshCaches();
        UpdateWaitingBaseline(state.AgentStatus);
    }

    /// <summary>
    /// Applies one ordered <c>step_batch</c> / <c>agent_status</c> envelope, but only when its seq is
    /// exactly the expected next number (cached BaseSeq + 1). Any discontinuity - gap, duplicate,
    /// identity change, inconsistent tick or a patch the reducer rejects - returns false and, because
    /// the stream can no longer be trusted, sets <see cref="NeedsResync"/> without touching the
    /// display state. Once that flag is set, every further envelope is refused until the caller passes
    /// a new snapshot to <see cref="RenderSnapshot"/>: a discontinuous patch is never merged into a
    /// world it does not belong to.
    /// </summary>
    public bool TryApply(object envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_state is null || _reducer is null || _needsResync)
        {
            _rejectReason = _state is null ? "no_snapshot" : "resync_required";
            return false;
        }

        switch (envelope)
        {
            case StepBatchMessage batch: return ApplyOrdered(batch, batch.Seq, "step_batch");
            case AgentStatusMessage status: return ApplyOrdered(status, status.Seq, "agent_status");
            default:
                // Not a stream discontinuity: the caller handed over an object this view cannot use,
                // so the baseline stays valid and the caller may simply correct itself.
                _rejectReason = "unsupported_envelope";
                return false;
        }
    }

    /// <summary>
    /// Renders the current frame: map, status line and recent semantic events, clipped to
    /// <see cref="Width"/> and <see cref="Height"/>, with no trailing blank line. In colour mode the
    /// frame starts with hide-cursor plus cursor-home and each line is terminated with erase-to-end
    /// (and the last line with erase-below) so shrinking frames leave no residue. The method never
    /// mutates the display state; with no clock injected the output is a pure function of that state.
    /// </summary>
    public string RenderFrame()
    {
        int width = _width;
        int height = _height;
        var lines = new List<string>();
        if (_state is null)
        {
            AddLine(lines, height, TextLine("terminal view: waiting for observer snapshot", width, ColorDim));
        }
        else if (width < CompactWidthThreshold)
        {
            BuildCompact(lines, width, height);
        }
        else
        {
            BuildFull(lines, width, height);
        }

        return Compose(lines);
    }

    /// <summary>
    /// Writes <see cref="RenderFrame"/> to the configured sink and flushes it. Colour frames position
    /// the cursor themselves, so a separator LF is added only for plain-text output; nothing else is
    /// ever written.
    /// </summary>
    public void Draw()
    {
        TextWriter? writer = _writer;
        if (writer is null) return;
        writer.Write(RenderFrame());
        if (!_useColor) writer.Write('\n');
        writer.Flush();
    }

    /// <summary>
    /// ANSI that restores the terminal after the last frame (attributes reset, cursor shown, one LF so
    /// the shell prompt does not overwrite the view). Empty when colours are disabled.
    /// </summary>
    public string RenderRestoreSequence() => _useColor ? AnsiReset + AnsiShowCursor + "\n" : "";

    /// <summary>
    /// Drops the whole display state - baseline, tiles, bounds, event log, feedback and the
    /// animation/timing caches - and marks the view as needing a fresh snapshot. The configured sink
    /// and layout are kept, so a resynchronising caller can reuse the same instance.
    /// </summary>
    public void Reset()
    {
        _reducer = null;
        _state = null;
        _tiles.Clear();
        _events.Clear();
        _lastFeedback = "none";
        _minX = 0;
        _minY = 0;
        _maxX = 0;
        _maxY = 0;
        _hasBounds = false;
        _playerX = 0;
        _playerY = 0;
        _hasPlayer = false;
        _waitingSince = TimeSpan.Zero;
        _waitingSinceValid = false;
        _rejectReason = null;
        _needsResync = true;
    }

    public void Dispose()
    {
        if (_ownsWriter)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // A sink that is already gone must not crash shutdown.
            }
        }

        _writer = null;
        _ownsWriter = false;
    }

    private bool ApplyOrdered(object envelope, long seq, string kind)
    {
        SnapshotMessage state = _state!;
        // Sequence continuity: the baseline covers base_seq, and observer/1 says the next envelope of
        // this subscription must be base_seq + 1. Duplicates and gaps are refused here, before the
        // reducer is touched, so no patch is ever applied to a world it does not follow.
        if (state.BaseSeq >= MaxSafeInteger || seq != state.BaseSeq + 1)
        {
            MarkResync(seq <= state.BaseSeq ? "duplicate_or_stale_seq" : "sequence_gap");
            return false;
        }

        try
        {
            _reducer!.Apply(envelope);
        }
        catch (ProtocolException error)
        {
            // The reducer is transactional, so its state is unchanged; an inconsistent identity, tick
            // or patch means this subscription can no longer be trusted either.
            MarkResync($"{kind}_rejected: {error.Message}");
            return false;
        }

        SnapshotMessage next = _reducer!.Snapshot();
        _state = next;
        if (envelope is StepBatchMessage batch)
        {
            AppendEvents(batch);
            _lastFeedback = DescribeFeedback(batch.Events);
        }

        RefreshCaches();
        UpdateWaitingBaseline(next.AgentStatus);
        _rejectReason = null;
        return true;
    }

    private void MarkResync(string reason)
    {
        // The display state is deliberately kept: the caller may keep showing the last trusted frame
        // until the replacement snapshot arrives, but nothing is merged in the meantime.
        _needsResync = true;
        _rejectReason = reason;
    }

    private void AppendEvents(StepBatchMessage batch)
    {
        foreach (ObserverEventDto item in batch.Events)
        {
            _events.Add($"t{Num(batch.Tick)} {DescribeEvent(item)}");
            if (_events.Count > MaxEvents) _events.RemoveAt(0);
        }
    }

    /// <summary>
    /// Rebuilds the tile index, bounds and camera target from the cached state. Tiles are keyed by
    /// coordinate and keep their <c>last_seen_tick</c>; visibility is never recomputed from radius or
    /// geometry, only read from the wire.
    /// </summary>
    private void RefreshCaches()
    {
        _tiles.Clear();
        SnapshotMessage? state = _state;
        if (state is not null)
        {
            foreach (ObserverTile tile in state.State.Tiles) _tiles[(tile.X, tile.Y)] = tile;
        }

        RecomputeBounds();

        _hasPlayer = false;
        if (state is not null)
        {
            // The live agent view publishes the stable id "player"; the frozen observer fixture uses
            // the equally stable "agent". Both name the single agent, and an exact "player" match wins.
            foreach (ObserverEntity entity in state.State.Entities)
            {
                if (!IsPlayer(entity)) continue;
                _playerX = entity.X;
                _playerY = entity.Y;
                _hasPlayer = true;
                if (string.Equals(entity.Id, "player", StringComparison.Ordinal)) break;
            }
        }

        if (_hasPlayer) return;
        // No player entity: keep the camera inside display memory instead of at a guessed tile.
        if (_hasBounds)
        {
            _playerX = (_minX + _maxX) / 2;
            _playerY = (_minY + _maxY) / 2;
        }
        else
        {
            _playerX = 0;
            _playerY = 0;
        }
    }

    private static bool IsPlayer(ObserverEntity entity) =>
        string.Equals(entity.Id, "player", StringComparison.Ordinal) ||
        string.Equals(entity.Id, "agent", StringComparison.Ordinal) ||
        string.Equals(entity.Kind, "player", StringComparison.Ordinal) ||
        string.Equals(entity.Kind, "agent", StringComparison.Ordinal);

    private void RecomputeBounds()
    {
        _hasBounds = _tiles.Count > 0;
        if (!_hasBounds)
        {
            _minX = 0;
            _minY = 0;
            _maxX = 0;
            _maxY = 0;
            return;
        }

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        foreach (var (x, y) in _tiles.Keys)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        _minX = minX;
        _minY = minY;
        _maxX = maxX;
        _maxY = maxY;
    }

    /// <summary>
    /// Timing cache for the waiting indicator. Elapsed time is measured from the moment the stream
    /// first reported <c>waiting</c>, so a repeated waiting status does not restart the clock. The
    /// cache is display-only: the view never publishes a pulse.
    /// </summary>
    private void UpdateWaitingBaseline(AgentStatusDto status)
    {
        bool waiting = status == AgentStatusDto.Waiting;
        if (!waiting || !_waitingSinceValid) _waitingSince = _clock is null ? TimeSpan.Zero : _clock();
        _waitingSinceValid = waiting;
    }

    private string? ElapsedSuffix()
    {
        if (_clock is null || !_waitingSinceValid || _state?.AgentStatus != AgentStatusDto.Waiting) return null;
        TimeSpan elapsed = _clock() - _waitingSince;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        string text = elapsed.TotalSeconds < 60
            ? elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
            : ((long)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m" +
              elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture) + "s";
        return $" ({text})";
    }

    private void BuildFull(List<string> lines, int width, int height)
    {
        SnapshotMessage state = _state!;
        AddLine(lines, height, HeaderLine(state, width));
        AddLine(lines, height, TextLine($"last: {_lastFeedback}", width, ColorFeedback));

        int remaining = height - lines.Count;
        if (remaining <= 0) return;

        int interior = Math.Max(1, width - 2); // the map block draws a border on both sides
        int naturalCols = _hasBounds ? _maxX - _minX + 1 : 1;
        int naturalRows = _hasBounds ? _maxY - _minY + 1 : 1;
        int viewCols = Math.Min(interior, naturalCols);

        // Give the map most of the room but always keep one row for the events header.
        int mapRows = Math.Min(Math.Min(naturalRows, Math.Max(1, remaining * 3 / 5)), remaining);
        int originX = OriginFor(_playerX, viewCols, _minX, _maxX);
        int originY = OriginFor(_playerY, mapRows, _minY, _maxY);
        for (int row = 0; row < mapRows; row++)
            AddLine(lines, height, MapRow(originX, originY + row, viewCols, interior, borders: true));

        int eventBlock = remaining - mapRows;
        if (eventBlock < 1) return;
        AddLine(lines, height, TextLine(_events.Count == 0 ? "events: none" : "events:", width, ColorLabel));
        int eventRows = Math.Min(MaxEvents, eventBlock - 1);
        // Oldest of the retained events first, so the newest sits at the bottom of the block.
        for (int index = Math.Max(0, _events.Count - eventRows); index < _events.Count; index++)
            AddLine(lines, height, TextLine(_events[index], width, ColorDim));
    }

    /// <summary>
    /// Degraded layout for narrow terminals: a status summary first, then - only when there is room -
    /// a borderless window around the player. Every line is clipped to the requested width, so a
    /// 1-column terminal still renders without an exception or an overlong line.
    /// </summary>
    private void BuildCompact(List<string> lines, int width, int height)
    {
        SnapshotMessage state = _state!;
        AddLine(lines, height, TextLine($"t={Num(state.Tick)} {PhaseText(state.State.MissionPhase)}", width, ColorLabel));
        AddLine(lines, height, TextLine(
            $"agent {StatusText(state.AgentStatus)}{ElapsedSuffix()}", width, _needsResync ? ColorAlert : ColorLabel));
        AddLine(lines, height, TextLine($"inv {InventoryText(state.State.Inventory)}", width, ColorLabel));
        AddLine(lines, height, TextLine($"last {_lastFeedback}", width, ColorFeedback));

        int remaining = height - lines.Count;
        if (width < MapMinWidth || remaining <= 0) return;

        int naturalRows = _hasBounds ? _maxY - _minY + 1 : 1;
        int viewCols = Math.Min(width, _hasBounds ? _maxX - _minX + 1 : 1);
        int viewRows = Math.Max(1, Math.Min(remaining, naturalRows));
        int originX = OriginFor(_playerX, viewCols, _minX, _maxX);
        int originY = OriginFor(_playerY, viewRows, _minY, _maxY);
        for (int row = 0; row < viewRows && lines.Count < height; row++)
            AddLine(lines, height, MapRow(originX, originY + row, viewCols, width, borders: false));
    }

    private string HeaderLine(SnapshotMessage state, int width)
    {
        var text = new StringBuilder();
        // The desync marker leads the line so that it survives clipping in a narrow terminal.
        if (_needsResync) text.Append(_rejectReason is null ? "!resync " : $"!resync({Sanitize(_rejectReason)}) ");
        text.Append("tick=").Append(Num(state.Tick));
        text.Append(" phase=").Append(PhaseText(state.State.MissionPhase));
        text.Append(" agent=").Append(StatusText(state.AgentStatus)).Append(ElapsedSuffix());
        text.Append(" inv=").Append(InventoryText(state.State.Inventory));
        text.Append(" seq=").Append(Num(state.BaseSeq));
        if (!string.IsNullOrEmpty(state.RunId)) text.Append(" run=").Append(Sanitize(state.RunId));
        return TextLine(text.ToString(), width, _needsResync ? ColorAlert : ColorHeader);
    }

    /// <summary>
    /// One map row: <paramref name="contentCols"/> cells starting at <paramref name="originX"/>,
    /// padded with blanks to <paramref name="interiorCols"/> and optionally framed. The visible length
    /// is exactly <c>interiorCols</c> (plus borders), which keeps it inside the requested width.
    /// </summary>
    private string MapRow(int originX, int y, int contentCols, int interiorCols, bool borders)
    {
        var row = new StringBuilder(interiorCols + 2);
        if (borders) row.Append('|');
        for (int column = 0; column < contentCols; column++)
        {
            (char glyph, string color) = Cell(originX + column, y);
            if (_useColor) row.Append(color);
            row.Append(glyph);
            if (_useColor) row.Append(AnsiReset);
        }

        for (int column = contentCols; column < interiorCols; column++) row.Append(' ');
        if (borders) row.Append('|');
        return row.ToString();
    }

    /// <summary>
    /// Glyph and colour of one cell. Only <c>last_seen_tick</c> decides current-visible versus
    /// remembered, and only real tile records produce terrain: a coordinate without a record is
    /// unknown ('?') instead of any known terrain.
    /// <para>
    /// Memory alphabet (remembered cells, always dimmed when colours are on, so a no-colour sink can
    /// still never mistake memory for current truth): floor '.' -> ',', wall '#' -> '%', key 'k' ->
    /// 'K', core 'C' -> 'c', closed door '+' -> '=', open door '/' -> '\', exit '>' -> 'v'.
    /// </para>
    /// </summary>
    private (char Glyph, string Color) Cell(int x, int y)
    {
        if (_hasPlayer && x == _playerX && y == _playerY) return ('@', ColorPlayer);
        if (!_tiles.TryGetValue((x, y), out ObserverTile? tile)) return ('?', ColorDim);

        bool visible = tile.LastSeenTick is null;
        if (tile.Item == ItemKindDto.Key) return visible ? ('k', ColorKey) : ('K', ColorDim);
        if (tile.Item == ItemKindDto.Core) return visible ? ('C', ColorCore) : ('c', ColorDim);
        if (tile.DoorOpen is bool open)
            return visible
                ? (open ? '/' : '+', open ? ColorDoorOpen : ColorDoorClosed)
                : (open ? '\\' : '=', ColorDim);
        if (tile.IsExit) return visible ? ('>', ColorExit) : ('v', ColorDim);
        if (tile.Terrain == TerrainDto.Wall) return visible ? ('#', ColorWall) : ('%', ColorDim);
        return visible ? ('.', ColorFloor) : (',', ColorDim);
    }

    /// <summary>
    /// Camera placement: centre the window on the focus tile, then clamp it so the window never leaves
    /// the remembered extents (and never addresses a column outside them).
    /// </summary>
    private static int OriginFor(int focus, int size, int min, int max)
    {
        int origin = focus - size / 2;
        int lowest = max - size + 1;
        if (lowest < min) lowest = min;
        if (origin < min) origin = min;
        if (origin > lowest) origin = lowest;
        return origin;
    }

    private string TextLine(string text, int width, string color)
    {
        string clipped = Clip(Sanitize(text), width);
        return _useColor ? color + clipped + AnsiReset : clipped;
    }

    /// <summary>Appends a line only while the requested height still has room.</summary>
    private static void AddLine(List<string> lines, int height, string line)
    {
        if (lines.Count < height) lines.Add(line);
    }

    private static string Clip(string text, int width)
    {
        if (width <= 0) return "";
        return text.Length <= width ? text : text[..width];
    }

    /// <summary>Canonical, culture-independent digits: the display never changes with the OS locale.</summary>
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders wire text (run id, subjects, reasons) as safe single-column ASCII: control characters -
    /// ESC above all - are neutralised so external text can never inject ANSI or break the layout, and
    /// non-ASCII characters are replaced so one character is always one column.
    /// </summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var safe = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            safe.Append(character is >= ' ' and <= '~' ? character : '?');
        }

        return safe.ToString();
    }

    private string Compose(List<string> lines)
    {
        if (!_useColor) return string.Join("\n", lines);
        var frame = new StringBuilder();
        frame.Append(AnsiHideCursor).Append(AnsiHome);
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) frame.Append('\n');
            frame.Append(lines[index]);
            frame.Append(index == lines.Count - 1 ? AnsiClearBelow : AnsiClearLine);
        }

        return frame.ToString();
    }

    /// <summary>
    /// Wording for the last action, derived from the semantic events the publisher already decided -
    /// no rule is re-evaluated here. Terminal outcomes outrank effects, which outrank movement.
    /// </summary>
    private static string DescribeFeedback(ObserverEventDto[] events)
    {
        if (events.Length == 0) return "none";
        ObserverEventDto best = events[0];
        foreach (ObserverEventDto item in events)
            if (FeedbackRank(item.Type) < FeedbackRank(best.Type)) best = item;
        return DescribeEvent(best);
    }

    private static int FeedbackRank(ObserverEventTypeDto type) => type switch
    {
        ObserverEventTypeDto.Succeeded => 0,
        ObserverEventTypeDto.TurnLimitReached => 1,
        ObserverEventTypeDto.MoveBlocked => 2,
        ObserverEventTypeDto.PickupNoEffect => 3,
        ObserverEventTypeDto.InteractionNoEffect => 4,
        ObserverEventTypeDto.DoorOpened => 5,
        ObserverEventTypeDto.PickedUp => 6,
        ObserverEventTypeDto.MissionPhaseChanged => 7,
        ObserverEventTypeDto.Moved => 8,
        ObserverEventTypeDto.Waited => 9,
        _ => 10
    };

    private static string DescribeEvent(ObserverEventDto item)
    {
        string at = item.Position is null ? "" : $" @({Num(item.Position.X)},{Num(item.Position.Y)})";
        string reason = string.IsNullOrEmpty(item.Reason) ? "" : $" ({item.Reason})";
        return item.Type switch
        {
            ObserverEventTypeDto.Moved => $"moved to{at}",
            ObserverEventTypeDto.MoveBlocked => $"move blocked{reason}{at}",
            ObserverEventTypeDto.PickedUp => $"picked up {Subject(item)}",
            ObserverEventTypeDto.DoorOpened => $"opened door{at}",
            ObserverEventTypeDto.InteractionNoEffect => $"interact no effect{reason}{at}",
            ObserverEventTypeDto.PickupNoEffect => "pickup nothing here",
            ObserverEventTypeDto.Waited => "waited",
            ObserverEventTypeDto.MissionPhaseChanged => $"phase -> {PhaseText(item.Phase)}",
            ObserverEventTypeDto.Succeeded => "mission succeeded",
            ObserverEventTypeDto.TurnLimitReached => "turn limit reached",
            _ => "unclassified event"
        };
    }

    private static string Subject(ObserverEventDto item) => item.Subject ?? item.EntityId ?? "item";

    private static string PhaseText(MissionPhaseDto? phase) => phase switch
    {
        MissionPhaseDto.FindKey => "find_key",
        MissionPhaseDto.OpenDoor => "open_door",
        MissionPhaseDto.FindCore => "find_core",
        MissionPhaseDto.ReturnToExit => "return_to_exit",
        MissionPhaseDto.Succeeded => "succeeded",
        _ => "unknown"
    };

    private static string StatusText(AgentStatusDto status) => status switch
    {
        AgentStatusDto.Waiting => "waiting",
        AgentStatusDto.ActionReceived => "action_received",
        AgentStatusDto.Stopped => "stopped",
        AgentStatusDto.ResyncRequired => "resync_required",
        AgentStatusDto.Errored => "errored",
        _ => "unknown"
    };

    private static string ItemText(ItemKindDto item) => item switch
    {
        ItemKindDto.Key => "key",
        ItemKindDto.Core => "core",
        _ => "unknown"
    };

    private static string InventoryText(ItemKindDto[] inventory)
    {
        if (inventory.Length == 0) return "-";
        var text = new StringBuilder();
        for (int index = 0; index < inventory.Length; index++)
        {
            if (index > 0) text.Append('+');
            text.Append(ItemText(inventory[index]));
        }

        return text.ToString();
    }
}
