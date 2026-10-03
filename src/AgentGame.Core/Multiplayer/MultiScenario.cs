using System.Collections.Immutable;

namespace AgentGame.Core.Multiplayer;

/// <summary>
/// Immutable initial layout for the multi-seat facility-zero/2 rules version. It reuses
/// the single-player <see cref="Scenario"/> as the single source of terrain and shared-object
/// truth, and layers the per-seat spawns on top (with the first spawn acting as the shared
/// layout's start). Every spawn must be on an in-bounds floor tile and must not coincide with
/// the shared key, door or core; spawns may overlap each other and the exit (cooperative
/// stacking is allowed per the confirmed decisions).
/// </summary>
public sealed class MultiScenario
{
    public const string CurrentRulesVersion = "facility-zero/2";
    public string RulesVersion => CurrentRulesVersion;

    /// <summary>The shared single-player layout that owns terrain, exit, key, door and core.</summary>
    public Scenario Layout { get; }
    public int SeatCount => Spawns.Length;
    public ImmutableArray<Position> Spawns { get; }

    public MultiScenario(IEnumerable<string> rows, IEnumerable<Position> spawns,
        Position exit, Position key, Position door, Position core,
        int maxTicks = 512, int visibilityRadius = 3)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(spawns);
        Position[] seatPositions = spawns.ToArray();
        if (seatPositions.Length is < 1 or > 4)
            throw new ArgumentException("Seat count must be between 1 and 4.", nameof(spawns));
        if (seatPositions.Distinct().Count() == 0)
            throw new ArgumentException("At least one distinct spawn is required.", nameof(spawns));

        // Reuse the v1 Scenario ctor as the single terrain/shared-object validator.
        Layout = new Scenario(rows, seatPositions[0], exit, key, door, core, maxTicks, visibilityRadius);
        foreach (Position spawn in seatPositions)
        {
            if (!Layout.Contains(spawn) || Layout.At(spawn) != Terrain.Floor)
                throw new ArgumentException("Every spawn must be on an in-bounds floor tile.", nameof(spawns));
            if (spawn == key || spawn == door || spawn == core)
                throw new ArgumentException("A spawn cannot overlap the shared key, door or core.", nameof(spawns));
        }
        Spawns = seatPositions.ToImmutableArray();
    }
}