using AgentGame.Cli;
using AgentGame.Protocol;

// M5 acceptance: the terminal view and replay controls live under in-repo contract checks.
// The two product types are internal, so this project relies on InternalsVisibleTo
// (AssemblyInfo.cs) and renders every frame to a StringWriter - never to the real console.

var tests = new (string Name, Action Run)[]
{
    ("TerminalView renders visible, remembered and unknown cells without ANSI when colourless", () =>
    {
        // A 3-column world so the whole map fits the drawn window: (1,0) is currently visible
        // floor, (3,0) is remembered floor, and x=2 has no tile record at all.
        SnapshotMessage snapshot = Baseline(tick: 0, baseSeq: 0, tiles:
        [
            Tile(1, 0, TerrainDto.Floor, LastSeenTick: null),
            Tile(3, 0, TerrainDto.Floor, LastSeenTick: 0)
        ]);

        var plain = new StringWriter();
        using (var view = new TerminalView(plain, width: 80, height: 24, useColor: false))
        {
            view.RenderSnapshot(snapshot);
            string frame = view.RenderFrame();
            // Memory alphabet keeps a no-colour sink honest: visible floor '.' vs remembered ','.
            Require(frame.Contains('.'), "Visible floor tile did not render as '.'.");
            Require(frame.Contains(','), "Remembered floor tile did not render as ','.");
            Require(frame.Contains('?'), "Unknown coordinate did not render as '?'.");
            Require(!frame.Contains('#') && !frame.Contains('%'), "Unknown coordinate leaked a floor/wall glyph.");
            Require(!frame.Contains('\u001b'), "Colourless frame contains an ESC byte.");
            Require(frame.IndexOf('.') != frame.IndexOf(','), "Visible and remembered tiles rendered the same glyph.");
        }
    }),

    ("TerminalView advances the frame, then refuses gap envelopes until a fresh snapshot", () =>
    {
        // All tiles remembered so a step with an empty visible set is transactionally consistent.
        SnapshotMessage first = Baseline(tick: 0, baseSeq: 5, tiles:
        [
            Tile(0, 0, TerrainDto.Floor, LastSeenTick: 0),
            Tile(1, 0, TerrainDto.Wall, LastSeenTick: 0)
        ]);

        var sink = new StringWriter();
        using var view = new TerminalView(sink, width: 80, height: 24, useColor: false);
        view.RenderSnapshot(first);
        string before = view.RenderFrame();

        // The next ordered envelope at base_seq + 1 advances the frame.
        Require(view.TryApply(Step(tick: 1, seq: 6, patch: new ObserverPatch
        {
            Inventory = [ItemKindDto.Key],
            MissionPhase = MissionPhaseDto.OpenDoor,
            VisibleTiles = []
        }, events: [Event(ObserverEventTypeDto.Moved, 1, 0)])),
            "Applying the next step_batch at base_seq + 1 was refused.");
        Require(!view.NeedsResync, "Valid envelope wrongly flagged a resync.");
        string advanced = view.RenderFrame();
        Require(!string.Equals(before, advanced, StringComparison.Ordinal), "Frame did not advance after a valid envelope.");

        // A gap (next expected seq is 7, we supply 12) is refused and pins the display state.
        Require(!view.TryApply(Step(tick: 2, seq: 12, patch: EmptyPatch(), events: [])),
            "Gap envelope was not refused.");
        Require(view.NeedsResync, "Gap envelope did not set NeedsResync.");
        // The trusted display state (map, events, tick, seq) is pinned; the only change is the
        // deliberately-added "!resync(...)" banner on the header line (documented behaviour).
        string[] beforeLines = advanced.Split('\n');
        string[] afterLines = view.RenderFrame().Split('\n');
        Require(afterLines.Length == beforeLines.Length, "Refused gap envelope changed the number of lines.");
        Require(afterLines[0].StartsWith("!resync(", StringComparison.Ordinal), "Resync banner not shown on the header.");
        for (int index = 1; index < afterLines.Length; index++)
            Require(string.Equals(beforeLines[index], afterLines[index], StringComparison.Ordinal),
                "Refused gap envelope mutated a non-header display line.");

        // Every further envelope stays refused until a fresh snapshot resets the baseline.
        Require(!view.TryApply(Step(tick: 2, seq: 7, patch: EmptyPatch(), events: [])),
            "Envelope after a gap was not refused.");
        Require(!view.TryApply(Step(tick: 2, seq: 7, patch: EmptyPatch(), events: [])),
            "Second envelope after a gap was not refused.");

        SnapshotMessage reset = Baseline(tick: 0, baseSeq: 20, tiles:
        [
            Tile(0, 0, TerrainDto.Floor, LastSeenTick: 0),
            Tile(1, 0, TerrainDto.Wall, LastSeenTick: 0)
        ]);
        view.RenderSnapshot(reset);
        Require(!view.NeedsResync, "Fresh snapshot did not clear NeedsResync.");
        Require(view.TryApply(Step(tick: 1, seq: 21, patch: EmptyPatch(), events: [])),
            "Envelope still refused after a fresh snapshot.");
    }),

    ("TerminalView handles every documented layout size without throwing or overflowing", () =>
    {
        SnapshotMessage snapshot = Baseline(tick: 3, baseSeq: 0, tiles:
        [
            Tile(0, 0, TerrainDto.Floor, LastSeenTick: null),
            Tile(1, 0, TerrainDto.Wall, LastSeenTick: null)
        ]);
        int[] widths = [1, 2, 10, 39, 40, 80];
        int[] heights = [1, 5, 24];
        foreach (int width in widths)
        foreach (int height in heights)
        {
            var sink = new StringWriter();
            using var view = new TerminalView(sink, useColor: false)
            {
                Width = width,
                Height = height
            };
            view.RenderSnapshot(snapshot);
            string frame = view.RenderFrame();
            foreach (string line in frame.Split('\n'))
                Require(line.Length <= width, $"Layout {width}x{height} produced an overlong line.");
        }
    }),

    ("ReplayControls clamps speed and derives delay from the nominal frame interval", () =>
    {
        Require(new ReplayControls(0.1).Speed == 0.25d, "Speed not clamped to the minimum 0.25.");
        Require(new ReplayControls(1000).Speed == 16.0d, "Speed not clamped to the maximum 16.");
        var nominal = new ReplayControls(1.0);
        Require(nominal.Speed == 1.0d, "Default speed is not 1.");
        Require(nominal.NextDelay() == TimeSpan.FromMilliseconds(250),
            "At speed 1 the delay is not the nominal frame interval.");

        var doubled = new ReplayControls(2.0);
        Require(doubled.NextDelay() == TimeSpan.FromMilliseconds(125),
            "Doubling speed did not halve the delay.");

        // Even a pathological slow speed can never ask for more than 5 seconds.
        var slow = new ReplayControls(0.25, TimeSpan.FromSeconds(20));
        Require(slow.NextDelay() == TimeSpan.FromSeconds(5), "Delay exceeded the 5 second cap.");
    }),

    ("ReplayControls honours pause, single-step, speed and quit keys", () =>
    {
        var c = new ReplayControls();
        // Space toggles pause.
        Require(Handle(c, ConsoleKey.Spacebar), "Space did not toggle pause.");
        Require(c.Paused, "Space did not pause.");
        Require(Handle(c, ConsoleKey.Spacebar), "Second Space did not toggle pause.");
        Require(!c.Paused, "Second Space did not resume.");

        // Single step while paused consumes exactly one pending step and returns zero delay.
        c = new ReplayControls();
        Handle(c, ConsoleKey.Spacebar);   // paused
        Handle(c, ConsoleKey.OemPeriod, '.');  // queue one step
        Require(c.Paused, "Step-clearing setup lost the paused flag.");
        Require(c.PendingSteps == 1, "Single step did not queue one pending step.");
        Require(c.NextDelay() == TimeSpan.Zero, "Pending step did not consume to a zero delay.");
        Require(c.PendingSteps == 0, "Pending step count did not drop by exactly one.");
        Require(c.NextDelay() == TimeSpan.Zero, "Empty paused delay is not zero.");
        Require(c.Paused, "Paused flag was cleared without input.");

        // '.' and RightArrow both queue a step; unknown keys change nothing.
        c = new ReplayControls();
        Require(Handle(c, ConsoleKey.OemPeriod, '.'), "'.' key not handled.");
        Require(Handle(c, ConsoleKey.RightArrow), "RightArrow key not handled.");
        Require(c.PendingSteps == 2, "Step keys did not queue two pending steps.");
        double speedBefore = c.Speed;
        bool pauseBefore = c.Paused;
        bool quitBefore = c.QuitRequested;
        Require(!Handle(c, ConsoleKey.X), "Unknown key was treated as handled.");
        Require(c.Speed == speedBefore && c.Paused == pauseBefore && c.QuitRequested == quitBefore
            && c.PendingSteps == 2, "Unknown key mutated control state.");

        // '+'/'-' change speed; 'q'/Escape set QuitRequested.
        c = new ReplayControls(1.0);
        Require(Handle(c, ConsoleKey.OemPlus, '+'), "'+' key not handled.");
        Require(c.Speed == 2.0d, "'+' did not double speed.");
        Require(Handle(c, ConsoleKey.OemMinus, '-'), "'-' key not handled.");
        Require(c.Speed == 1.0d, "'-' did not halve speed.");
        Require(Handle(c, ConsoleKey.Q, 'q'), "'q' key not handled.");
        Require(c.QuitRequested, "'q' did not set QuitRequested.");
        c = new ReplayControls();
        Require(Handle(c, ConsoleKey.Escape), "Escape key not handled.");
        Require(c.QuitRequested, "Escape did not set QuitRequested.");
    }),

    ("ReplayControls.PollInput degrades to a no-op when console input is unavailable", () =>
    {
        var c = new ReplayControls();
        c.PollInput();
        c.PollInput();
        Require(true, "PollInput threw when input was unavailable.");
    })
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine($"M5 Terminal: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

// ---- synthesis helpers (all pure; never touch the real console) ----

static SnapshotMessage Baseline(long tick, long baseSeq, ObserverTile[] tiles) => new()
{
    RunId = "m5-terminal",
    BaseSeq = baseSeq,
    Tick = tick,
    AgentStatus = AgentStatusDto.Waiting,
    State = new ObserverState
    {
        VisibleRadius = 1,
        Tiles = tiles,
        MissionPhase = MissionPhaseDto.FindKey
    }
};

static ObserverTile Tile(int x, int y, TerrainDto terrain, long? LastSeenTick) => new()
{
    X = x, Y = y, Terrain = terrain, LastSeenTick = LastSeenTick
};

static ObserverPatch EmptyPatch() => new()
{
    VisibleTiles = [],
    Inventory = [],
    MissionPhase = MissionPhaseDto.FindKey
};

static StepBatchMessage Step(long tick, long seq, ObserverPatch patch, ObserverEventDto[] events) => new()
{
    RunId = "m5-terminal",
    Seq = seq,
    Tick = tick,
    AgentStatus = AgentStatusDto.ActionReceived,
    Events = events,
    Patch = patch
};

static ObserverEventDto Event(ObserverEventTypeDto type, int x, int y) => new()
{
    Type = type,
    Position = new PointDto { X = x, Y = y },
    Reason = "m5"
};

static bool Handle(ReplayControls controls, ConsoleKey key, char keyChar = '\0') =>
    controls.TryHandleKey(new ConsoleKeyInfo(keyChar, key, false, false, false));

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}