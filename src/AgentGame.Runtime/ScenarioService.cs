using System.Globalization;
using System.Text;
using AgentGame.Core;
using AgentGame.Protocol;

namespace AgentGame.Runtime;

public sealed record ScenarioSummary(bool Valid, string? Error, string Generator, string? Seed,
    int? GenerationAttempts, int? ReferenceLength, string? InitialHash);

public sealed class ScenarioGenerationException(ulong seed, string generator, int attempts, string reason)
    : Exception($"Generation failed: {reason} (seed {seed}, generator {generator}, attempts {attempts}).")
{
    public ulong Seed { get; } = seed;
    public string Generator { get; } = generator;
    public int Attempts { get; } = attempts;
    public string Reason { get; } = reason;
}

/// <summary>Scenario file boundary and explicit Protocol/Core mapping. Oracle actions stay internal.</summary>
public static class ScenarioService
{
    public static ScenarioDto Generate(ulong seed, int maxTicks = 512, int maxReferenceLength = 128, int maxAttempts = 16)
    {
        GenerationResult result = ScenarioGenerator.Generate(seed,
            new GenerationOptions(MaxTicks: maxTicks, MaxReferenceLength: maxReferenceLength, MaxAttempts: maxAttempts));
        if (!result.Success)
            throw new ScenarioGenerationException(seed, result.Generator, result.Attempts, result.Error!);
        Scenario scene = result.Scenario!;
        return new ScenarioDto
        {
            Generator = result.Generator, Seed = seed.ToString(CultureInfo.InvariantCulture),
            GenerationAttempts = result.Attempts, ReferenceLength = result.Reference!.Actions.Length,
            Rows = Enumerable.Range(0, scene.Height).Select(y => new string(Enumerable.Range(0, scene.Width)
                .Select(x => scene.At(new(x, y)) == Terrain.Wall ? '#' : '.').ToArray())).ToArray(),
            Start = ToDto(scene.Start), Exit = ToDto(scene.Exit), Key = ToDto(scene.Key),
            Door = ToDto(scene.Door), Core = ToDto(scene.Core), MaxTicks = scene.MaxTicks, VisibilityRadius = scene.VisibilityRadius
        };
    }

    public static ScenarioSummary Validate(ScenarioDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ScenarioDto owned = ScenarioCodec.Parse(ScenarioCodec.Encode(dto));
        Scenario scene = ToCoreScenario(owned);
        ScenarioValidationResult result = ScenarioValidator.Validate(scene);
        string? error = result.Error;
        int? length = result.Reference?.Success == true ? result.Reference.Actions.Length : null;
        if (result.IsValid && owned.ReferenceLength is not null && owned.ReferenceLength != length)
            error = "reference_length_mismatch";
        return new(result.IsValid && error is null, error, owned.Generator, owned.Seed, owned.GenerationAttempts,
            length, StateEncoding.Hash(Game.Create(scene).Capture()));
    }

    public static ScenarioDto Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[ProtocolLimits.MaxLineBytes + 1];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = stream.Read(buffer.AsSpan(count));
            if (read == 0) break;
            count += read;
        }
        return ScenarioCodec.Parse(buffer.AsSpan(0, count));
    }

    public static void Write(string path, ScenarioDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        string json = ScenarioCodec.Encode(dto);
        _ = ScenarioCodec.Parse(json);
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(json);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    private static PointDto ToDto(Position p) => new() { X = p.X, Y = p.Y };
    private static Position ToCore(PointDto p) => new(p.X, p.Y);
    internal static Scenario ToCoreScenario(ScenarioDto dto) => new(dto.Rows, ToCore(dto.Start), ToCore(dto.Exit),
        ToCore(dto.Key), ToCore(dto.Door), ToCore(dto.Core), dto.MaxTicks, dto.VisibilityRadius);
}
