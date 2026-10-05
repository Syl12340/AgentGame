using System.Globalization;
using System.Text;
using AgentGame.Core;
using AgentGame.Core.Multiplayer;
using AgentGame.Protocol;

namespace AgentGame.Runtime;

/// <summary>
/// scenario/2 file boundary and explicit Protocol/Core mapping, mirroring
/// <see cref="ScenarioService"/>. Public Read/Write/Generate/Validate expose the frozen
/// multi-seat layout; the internal To*/From* helpers are the single mapping point the runner
/// and the replay verify path reuse so Core never sees raw Protocol types and vice versa.
/// </summary>
public static class MultiScenarioService
{
    /// <summary>Generate a cooperative multi-seat scenario. Default two seats (Core stays 1..4).</summary>
    public static MultiScenarioDto Generate(ulong seed, MultiGenerationOptions? options = null)
    {
        MultiGenerationOptions effective = options ?? new();
        MultiGenerationResult result = MultiScenarioGenerator.Generate(seed, effective);
        if (!result.Success)
            throw new ScenarioGenerationException(seed, result.Generator, result.Attempts, result.Error!);
        MultiScenario scene = result.Scenario!;
        return new MultiScenarioDto
        {
            Generator = result.Generator, Seed = seed.ToString(CultureInfo.InvariantCulture),
            GenerationAttempts = result.Attempts, ReferenceLength = result.Reference!.Actions.Length,
            Rows = Enumerable.Range(0, scene.Layout.Height).Select(y => new string(Enumerable.Range(0, scene.Layout.Width)
                .Select(x => scene.Layout.At(new(x, y)) == Terrain.Wall ? '#' : '.').ToArray())).ToArray(),
            Spawns = scene.Spawns.Select(p => new PointDto { X = p.X, Y = p.Y }).ToArray(),
            Exit = ToDto(scene.Layout.Exit), Key = ToDto(scene.Layout.Key),
            Door = ToDto(scene.Layout.Door), Core = ToDto(scene.Layout.Core),
            MaxTicks = scene.Layout.MaxTicks, VisibilityRadius = scene.Layout.VisibilityRadius
        };
    }

    public static ScenarioSummary Validate(MultiScenarioDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        MultiScenarioDto owned = MultiScenarioCodec.Parse(MultiScenarioCodec.Encode(dto));
        MultiScenario scene = ToCoreScenario(owned);
        MultiValidationResult result = MultiScenarioValidator.Validate(scene);
        string? error = result.Error;
        int? length = result.Reference?.Success == true ? result.Reference.Actions.Length : null;
        if (result.IsValid && owned.ReferenceLength is not null && owned.ReferenceLength != length)
            error = "reference_length_mismatch";
        return new(result.IsValid && error is null, error, owned.Generator, owned.Seed, owned.GenerationAttempts,
            length, MultiStateEncoding.Hash(MultiGame.Create(scene).Capture()));
    }

    public static MultiScenarioDto Read(string path)
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
        return MultiScenarioCodec.Parse(buffer.AsSpan(0, count));
    }

    public static void Write(string path, MultiScenarioDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        string json = MultiScenarioCodec.Encode(dto);
        _ = MultiScenarioCodec.Parse(json);
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(json);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    private static PointDto ToDto(Position p) => new() { X = p.X, Y = p.Y };

    internal static MultiScenario ToCoreScenario(MultiScenarioDto dto) => new(dto.Rows,
        dto.Spawns.Select(p => new Position(p.X, p.Y)), new(dto.Exit.X, dto.Exit.Y), new(dto.Key.X, dto.Key.Y),
        new(dto.Door.X, dto.Door.Y), new(dto.Core.X, dto.Core.Y), dto.MaxTicks, dto.VisibilityRadius);

    internal static EpisodeBody ToEpisode(EpisodeOutcome outcome) => new()
    { Kind = outcome.Kind == EpisodeEndKind.Success ? EpisodeKindDto.Success : EpisodeKindDto.TurnLimit };

    /// <summary>Strict action mapping with full enum/direction validation used by runner and verify.</summary>
    internal static GameAction ToCoreAction(ActionRequestDto action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Enum.IsDefined(action.Type)) throw new ArgumentException("Unknown action type.");
        bool directional = action.Type is ActionTypeDto.Move or ActionTypeDto.Interact;
        if (directional != action.Direction.HasValue || (action.Direction.HasValue && !Enum.IsDefined(action.Direction.Value)))
            throw new ArgumentException("Direction is required exactly for move/interact and must be defined.");
        return action.Type switch
        {
            ActionTypeDto.Move => GameAction.Move(ToCoreDirection(action.Direction!.Value)),
            ActionTypeDto.Interact => GameAction.Interact(ToCoreDirection(action.Direction!.Value)),
            ActionTypeDto.Pickup => GameAction.Pickup(),
            ActionTypeDto.Wait => GameAction.Wait(),
            _ => throw new ArgumentException("Unknown parsed action.")
        };
    }

    internal static AgentObservationBody ToObservation(MultiObservation observation)
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
            Episode = observation.Episode is null ? null : ToEpisode(observation.Episode)
        };
    }

    private static Direction ToCoreDirection(DirectionDto direction) => direction switch
    {
        DirectionDto.North => Direction.North, DirectionDto.East => Direction.East,
        DirectionDto.South => Direction.South, DirectionDto.West => Direction.West,
        _ => throw new ArgumentException("Unknown parsed direction.")
    };
}
