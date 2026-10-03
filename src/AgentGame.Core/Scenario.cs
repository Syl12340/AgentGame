using System.Collections.Immutable;

namespace AgentGame.Core;

/// <summary>Immutable initial layout. Structural checks only; full task validation belongs to M2.</summary>
public sealed class Scenario
{
    public const string CurrentRulesVersion = "facility-zero/1";
    public string RulesVersion => CurrentRulesVersion;
    public int Width { get; }
    public int Height { get; }
    public ImmutableArray<Terrain> Tiles { get; }
    public Position Start { get; }
    public Position Exit { get; }
    public Position Key { get; }
    public Position Door { get; }
    public Position Core { get; }
    public int MaxTicks { get; }
    public int VisibilityRadius { get; }

    public Scenario(IEnumerable<string> rows, Position start, Position exit, Position key,
        Position door, Position core, int maxTicks = 512, int visibilityRadius = 3)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string[] input = rows.ToArray();
        if (input.Length < 1 || input.Length > 128 || input[0] is null || input[0].Length < 1 || input[0].Length > 128)
            throw new ArgumentException("Map dimensions must be between 1 and 128.", nameof(rows));
        Width = input[0].Length;
        Height = input.Length;
        if (maxTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxTicks));
        if (visibilityRadius is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(visibilityRadius));
        MaxTicks = maxTicks;
        VisibilityRadius = visibilityRadius;
        var terrain = ImmutableArray.CreateBuilder<Terrain>(Width * Height);
        foreach (string row in input)
        {
            if (row is null || row.Length != Width) throw new ArgumentException("Rows must be rectangular.", nameof(rows));
            foreach (char cell in row)
                terrain.Add(cell switch
                {
                    '.' => Terrain.Floor,
                    '#' => Terrain.Wall,
                    _ => throw new ArgumentException("Terrain uses only '.' and '#'.", nameof(rows))
                });
        }
        Tiles = terrain.MoveToImmutable();
        Start = start; Exit = exit; Key = key; Door = door; Core = core;
        foreach (Position p in new[] { start, exit, key, door, core })
            if (!Contains(p) || At(p) != Terrain.Floor)
                throw new ArgumentException("Start, exit and objects must be on in-bounds floor tiles.");
        if (new[] { exit, key, door, core }.Distinct().Count() != 4 ||
            start == key || start == door || start == core)
            throw new ArgumentException("Objects cannot overlap; only start and exit may share a tile.");
    }

    public bool Contains(Position p) => p.X >= 0 && p.X < Width && p.Y >= 0 && p.Y < Height;
    public Terrain At(Position p)
    {
        if (!Contains(p)) throw new ArgumentOutOfRangeException(nameof(p));
        return Tiles[p.Y * Width + p.X];
    }
}