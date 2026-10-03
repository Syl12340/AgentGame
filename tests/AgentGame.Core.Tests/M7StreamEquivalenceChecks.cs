using AgentGame.Core;
using AgentGame.Core.Multiplayer;

namespace AgentGame.Core.Tests;

/// <summary>
/// Cross-checks that the multi generator's stream derivation truly matches the frozen v1 formula.
/// <c>MultiRandomStreams.Create(seed, name)</c> is pinned to <c>NamedRandomStreams.Create(seed, name)</c>
/// for every v1 stream name across several seeds, so the multi helper can never silently drift from v1.
/// Also asserts the extended allow-list (the multi-only "spawns" name works, any other name is rejected
/// with the same style as v1).
/// </summary>
internal static class M7StreamEquivalenceChecks
{
    public static async Task<int> RunAsync(string root)
    {
        _ = root;
        var checks = new List<(string Name, Func<Task> Run)>
        {
            ("MultiRandomStreams matches frozen v1 streams for map/objects/mission across seeds", StreamsMatchV1Async),
            ("MultiRandomStreams supports spawns and rejects unknown names like v1", AllowListAsync),
        };

        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try
            {
                await check();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {error.Message}");
            }
        }
        Console.WriteLine($"M7 stream equivalence: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static Task StreamsMatchV1Async()
    {
        var seeds = new ulong[] { 0UL, 1UL, 42UL, 999UL, ulong.MaxValue };
        string[] names = { "map", "objects", "mission" };
        const int outputs = 16;
        foreach (ulong seed in seeds)
        foreach (string name in names)
        {
            var multi = MultiRandomStreams.Create(seed, name);
            var v1 = NamedRandomStreams.Create(seed, name);
            for (int i = 0; i < outputs; i++)
            {
                uint a = multi.NextUInt32();
                uint b = v1.NextUInt32();
                if (a != b)
                    throw new Exception($"seed {seed} name '{name}' output {i}: multi {a} != v1 {b}.");
            }
        }
        Console.WriteLine($"  Seeds (0,1,42,999,Max) x names map/objects/mission: {seeds.Length * names.Length} streams x {outputs} outputs match v1.");
        return Task.CompletedTask;
    }

    private static Task AllowListAsync()
    {
        // The multi-only "spawns" stream derives and advances cleanly.
        var spawns = MultiRandomStreams.Create(42, "spawns");
        _ = spawns.NextUInt32();

        // V1's three names are accepted too.
        _ = MultiRandomStreams.Create(0, "map");
        _ = MultiRandomStreams.Create(0, "objects");
        _ = MultiRandomStreams.Create(0, "mission");

        // Anything else is rejected with the same ArgumentException style as v1.
        try { _ = MultiRandomStreams.Create(0, "unknown"); throw new Exception("unknown name was accepted."); }
        catch (ArgumentException) { }
        try { _ = NamedRandomStreams.Create(0, "unknown"); throw new Exception("v1 accepted unknown name."); }
        catch (ArgumentException) { }

        return Task.CompletedTask;
    }
}