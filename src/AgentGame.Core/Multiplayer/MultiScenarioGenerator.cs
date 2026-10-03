namespace AgentGame.Core.Multiplayer;

/// <summary>Generation options for a cooperative multi-seat facility scenario.</summary>
public sealed record MultiGenerationOptions(
    int Width = 15, int Height = 9, int MaxTicks = 512, int VisibilityRadius = 3,
    int MaxReferenceLength = 128, int MaxAttempts = 16, int SeatCount = 2);

/// <summary>
/// Deterministic generation outcome. On failure the original seed, generator version, the number
/// of attempts actually made and a stable reason are all carried, so a caller never silently sees
/// a different seed on retry.
/// </summary>
public sealed record MultiGenerationResult(
    bool Success, ulong Seed, string Generator, int Attempts,
    MultiScenario? Scenario, MultiSolveResult? Reference, string? Error);

/// <summary>
/// Deterministic generation of a cooperative 2..4-seat facility using the exact v1 four-room
/// topology: a pre-door region (entrance/exit, key) and the core region locked behind a mandatory
/// door, with one spawn per seat placed in the pre-door region. The layout reuses the v1
/// <c>map</c>/<c>objects</c>/<c>mission</c> streams in the identical draw order as
/// <see cref="AgentGame.Core.ScenarioGenerator"/> so those stream derivations are unchanged; only a
/// new named <c>spawns</c> stream is used for per-seat spawn placement.
/// <para>
/// The default board is 15&#215;9 (larger boards are permitted via options). A two-agent full-state
/// cooperative search over the real transitions is exponential in the per-seat floor-count, so a
/// compact board keeps generation and the cooperative solver fast while preserving the topology.
/// </para>
/// </summary>
public static class MultiScenarioGenerator
{
    public const string Version = "facility-multi-generation/1";

    /// <summary>
    /// New named random stream used only for per-seat spawn placement (added for M7 generation).
    /// The v1 <c>map</c>/<c>objects</c>/<c>mission</c> names keep their exact derivation in
    /// <see cref="AgentGame.Core.NamedRandomStreams"/> and are unconsumed otherwise, so v1 stream
    /// semantics are unchanged. The derivation matches randomness.md verbatim:
    /// SHA-256("generation-rng/1" NUL name NUL seed) little-endian halves -> PCG state/sequence.
    /// </summary>
    public const string SpawnStreamName = "spawns";

    public static MultiGenerationResult Generate(ulong seed, MultiGenerationOptions? options = null)
    {
        options ??= new();
        ValidateOptions(options);
        Pcg32 map = MultiRandomStreams.Create(seed, "map");
        Pcg32 objects = MultiRandomStreams.Create(seed, "objects");
        Pcg32 mission = MultiRandomStreams.Create(seed, "mission");
        Pcg32 spawns = MultiRandomStreams.Create(seed, SpawnStreamName);
        string error = "generation_exhausted";
        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            if (!TryBuild(options, map, objects, mission, spawns, out MultiScenario scenario, out string? buildError))
            {
                error = buildError!;
                continue;
            }
            MultiValidationResult validation = MultiScenarioValidator.Validate(scenario);
            if (validation.IsValid && validation.Reference!.Actions.Length <= options.MaxReferenceLength)
                return new(true, seed, Version, attempt, scenario, validation.Reference, null);
            error = validation.Error ?? "reference_length_exceeded";
        }
        return new(false, seed, Version, options.MaxAttempts, null, null, error);
    }

    private static void ValidateOptions(MultiGenerationOptions options)
    {
        if (options.Width is < 9 or > 128 || options.Height is < 7 or > 128)
            throw new ArgumentOutOfRangeException(nameof(options), "Generation dimensions must be 9..128 by 7..128.");
        if (options.MaxTicks < 1 || options.VisibilityRadius is < 0 or > 128 ||
            options.MaxReferenceLength < 1 || options.MaxAttempts is < 1 or > 64 ||
            options.SeatCount is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid tick, visibility, reference, retry or seat limit.");
    }

    private static bool TryBuild(MultiGenerationOptions options, Pcg32 map, Pcg32 objects, Pcg32 mission,
        Pcg32 spawns, out MultiScenario scenario, out string? error)
    {
        error = null;
        scenario = null!;
        int width = options.Width, height = options.Height;
        char[][] rows = Enumerable.Range(0, height).Select(y => Enumerable.Range(0, width)
            .Select(x => x == 0 || y == 0 || x == width - 1 || y == height - 1 ? '#' : '.').ToArray()).ToArray();
        int split = 3 + map.NextInt(width - 6);
        int doorY = 1 + map.NextInt(height - 2);
        for (int y = 1; y < height - 1; y++) rows[y][split] = '#';
        rows[doorY][split] = '.';
        int[] wallRows = Enumerable.Range(2, height - 4).Where(y => y != doorY).ToArray();
        int leftY = wallRows[map.NextInt(wallRows.Length)];
        int rightY = wallRows[map.NextInt(wallRows.Length)];
        for (int x = 1; x < split; x++) rows[leftY][x] = '#';
        rows[leftY][1 + map.NextInt(split - 1)] = '.';
        for (int x = split + 1; x < width - 1; x++) rows[rightY][x] = '#';
        rows[rightY][split + 1 + map.NextInt(width - split - 2)] = '.';

        var left = new List<Position>();
        var right = new List<Position>();
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
            if (rows[y][x] == '.')
            {
                if (x < split) left.Add(new(x, y));
                else if (x > split) right.Add(new(x, y));
            }

        // Layout positions consume the v1 streams in the identical order as ScenarioGenerator.
        int startIndex = objects.NextInt(left.Count);
        int keyIndex = objects.NextInt(left.Count - 1);
        if (keyIndex >= startIndex) keyIndex++;
        Position start = left[startIndex];
        Position key = left[keyIndex];
        Position door = new(split, doorY);
        Position core = right[mission.NextInt(right.Count)];
        Position exit = start;

        // One spawn per seat, all in the pre-door region, distinct, and never on key/door/core.
        // spawn[0] is the shared layout start (= exit, allowed by the cooperative rules).
        var used = new HashSet<Position> { start };
        var spawn = new List<Position> { start };
        List<Position> candidates = left.Where(p => p != start && p != key).ToList();
        for (int seat = 1; seat < options.SeatCount; seat++)
        {
            List<Position> available = candidates.Where(c => !used.Contains(c)).ToList();
            if (available.Count == 0) { error = "not_enough_spawn_tiles"; return false; }
            Position chosen = available[spawns.NextInt(available.Count)];
            spawn.Add(chosen);
            used.Add(chosen);
        }

        scenario = new MultiScenario(rows.Select(r => new string(r)), spawn, exit, key, door, core,
            options.MaxTicks, options.VisibilityRadius);
        return true;
    }
}