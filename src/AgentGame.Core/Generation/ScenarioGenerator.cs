namespace AgentGame.Core;

public sealed record GenerationOptions(int Width = 21, int Height = 13, int MaxTicks = 512,
    int VisibilityRadius = 3, int MaxReferenceLength = 128, int MaxAttempts = 16);

public sealed record GenerationResult(bool Success, ulong Seed, string Generator, int Attempts,
    Scenario? Scenario, SolveResult? Reference, string? Error);

/// <summary>Four connected rooms split by a mandatory locked door, with bounded retries.</summary>
public static class ScenarioGenerator
{
    public const string Version = "facility-generation/1";

    public static GenerationResult Generate(ulong seed, GenerationOptions? options = null)
    {
        options ??= new();
        ValidateOptions(options);
        Pcg32 map = NamedRandomStreams.Create(seed, "map");
        Pcg32 objects = NamedRandomStreams.Create(seed, "objects");
        Pcg32 mission = NamedRandomStreams.Create(seed, "mission");
        string error = "generation_exhausted";
        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            Scenario scenario = Build(options, map, objects, mission);
            ScenarioValidationResult validation = ScenarioValidator.Validate(scenario);
            if (validation.IsValid && validation.Reference!.Actions.Length <= options.MaxReferenceLength)
                return new(true, seed, Version, attempt, scenario, validation.Reference, null);
            error = validation.Error ?? "reference_length_exceeded";
        }
        return new(false, seed, Version, options.MaxAttempts, null, null, error);
    }

    private static void ValidateOptions(GenerationOptions options)
    {
        if (options.Width is < 9 or > 128 || options.Height is < 7 or > 128)
            throw new ArgumentOutOfRangeException(nameof(options), "Generation dimensions must be 9..128 by 7..128.");
        if (options.MaxTicks < 1 || options.VisibilityRadius is < 0 or > 128 ||
            options.MaxReferenceLength < 1 || options.MaxAttempts is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid tick, visibility, reference or retry limit.");
    }

    private static Scenario Build(GenerationOptions options, Pcg32 map, Pcg32 objects, Pcg32 mission)
    {
        int width = options.Width, height = options.Height;
        char[][] rows = Enumerable.Range(0, height).Select(y => Enumerable.Range(0, width)
            .Select(x => x == 0 || y == 0 || x == width - 1 || y == height - 1 ? '#' : '.').ToArray()).ToArray();
        int split = 3 + map.NextInt(width - 6);
        int doorY = 1 + map.NextInt(height - 2);
        for (int y = 1; y < height - 1; y++) rows[y][split] = '#';
        rows[doorY][split] = '.';
        // Internal passages divide each side into two rooms. Never cover a door neighbour.
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
        int startIndex = objects.NextInt(left.Count);
        int keyIndex = objects.NextInt(left.Count - 1);
        if (keyIndex >= startIndex) keyIndex++;
        Position start = left[startIndex];
        return new(rows.Select(row => new string(row)), start, start, left[keyIndex],
            new(split, doorY), right[mission.NextInt(right.Count)], options.MaxTicks, options.VisibilityRadius);
    }
}
