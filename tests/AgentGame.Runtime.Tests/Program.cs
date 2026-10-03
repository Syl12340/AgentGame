using System.Text;
using AgentGame.Protocol;
using AgentGame.Runtime;

string root = FindRoot();
string temporary = Path.Combine(root, "artifacts", "runtime-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var tests = new (string Name, Action Run)[]
{
    ("Manual scenario maps to known Core hash and shortest route", () =>
    {
        var result = ScenarioService.Validate(ScenarioService.Read(Fixture("facility-small.json")));
        Check(result.Valid, result.Error ?? "Rejected valid scenario."); Equal(13, result.ReferenceLength);
        Equal("932e1d591f8e19764177838757f3117c8dbcd19e202e47f806ff4bb50c49d286", result.InitialHash);
    }),
    ("Generated UInt64 metadata survives file roundtrip", () =>
    {
        var dto = ScenarioService.Generate(ulong.MaxValue);
        Equal("18446744073709551615", dto.Seed); Equal("facility-generation/1", dto.Generator);
        Check(dto.GenerationAttempts is >= 1 and <= 16 && dto.ReferenceLength is >= 1 and <= 128, "Metadata out of budget.");
        string path = Path.Combine(temporary, "roundtrip.json");
        ScenarioService.Write(path, dto);
        Check(File.ReadAllBytes(path)[0] == (byte)'{', "Unexpected BOM.");
        var reloaded = ScenarioService.Read(path);
        Equal(ScenarioCodec.Encode(dto), ScenarioCodec.Encode(reloaded));
        Equal(ScenarioService.Validate(dto), ScenarioService.Validate(reloaded));
    }),
    ("Imported topology errors are rejected", () =>
    {
        var key = ScenarioService.Validate(ScenarioService.Read(Fixture("invalid-key-behind-door.json")));
        Check(!key.Valid, "Key behind door accepted."); Equal("key_unreachable_before_door", key.Error);
        var bypass = ScenarioService.Validate(ScenarioService.Read(Fixture("invalid-door-bypass.json")));
        Check(!bypass.Valid, "Door bypass accepted."); Equal("door_not_required", bypass.Error);
    }),
    ("Supplied reference length must match actual shortest task", () =>
    {
        var dto = ScenarioService.Read(Fixture("facility-small.json")) with { ReferenceLength = 14 };
        var summary = ScenarioService.Validate(dto);
        Check(!summary.Valid, "False certificate accepted."); Equal("reference_length_mismatch", summary.Error);
    }),
    ("Programmatic DTOs pass strict structural validation", () =>
    {
        var dto = ScenarioService.Read(Fixture("facility-small.json"));
        Throws<ProtocolException>(() => ScenarioService.Validate(dto with { Rules = "unknown/1" }));
        Throws<ProtocolException>(() => ScenarioService.Validate(dto with { Rows = [] }));
    }),
    ("Bounded reads reject oversized files and invalid UTF8", () =>
    {
        string large = Path.Combine(temporary, "oversize.json");
        File.WriteAllBytes(large, Enumerable.Repeat((byte)' ', ProtocolLimits.MaxLineBytes + 1).ToArray());
        Throws<ProtocolException>(() => ScenarioService.Read(large));
        string malformed = Path.Combine(temporary, "bad-utf8.json"); File.WriteAllBytes(malformed, [0xc3, 0x28]);
        Throws<ProtocolException>(() => ScenarioService.Read(malformed));
    }),
    ("Write refuses overwrite and preserves existing contents", () =>
    {
        string path = Path.Combine(temporary, "existing.json"); File.WriteAllText(path, "preserve", new UTF8Encoding(false));
        Throws<IOException>(() => ScenarioService.Write(path, ScenarioService.Generate(42)));
        Equal("preserve", File.ReadAllText(path));
    }),
    ("Write rejects malformed DTO before creating output", () =>
    {
        string path = Path.Combine(temporary, "rejected.json");
        Throws<ProtocolException>(() => ScenarioService.Write(path, new ScenarioDto()));
        Check(!File.Exists(path), "Rejected input left a file behind.");
    }),
    ("Generation failure retains seed version attempts and reason", () =>
    {
        try { ScenarioService.Generate(42, maxReferenceLength: 1, maxAttempts: 3); }
        catch (ScenarioGenerationException error)
        {
            Equal(42UL, error.Seed); Equal("facility-generation/1", error.Generator); Equal(3, error.Attempts);
            Equal("reference_length_exceeded", error.Reason); return;
        }
        throw new Exception("Generation ignored budget.");
    }),
    ("Turn budget and invalid options remain distinct", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => ScenarioService.Generate(42, maxAttempts: 65));
        try { ScenarioService.Generate(42, maxTicks: 1, maxAttempts: 2); }
        catch (ScenarioGenerationException error) { Equal("no_solution_within_turn_limit", error.Reason); Equal(2, error.Attempts); return; }
        throw new Exception("Generation ignored turn budget.");
    })
};
int failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine($"Runtime: {tests.Length - failed}/{tests.Length} passed.");
int m3Failed = await M3Checks.RunAsync(root);
int m4Failed = M4ObserverChecks.Run(root) + M4ReplayChecks.Run(root) + M4HubChecks.Run(root);
 m4Failed += await M4IntegrationChecks.RunAsync(root);
 m4Failed += await M4CliChecks.RunAsync(root);
int m5Failed = await M5ExplorerChecks.RunAsync(root);
 m5Failed += await M5CliChecks.RunAsync(root);
int m6Failed = await M6GoldenChecks.RunAsync(root);
 m6Failed += await M6ProcessTreeChecks.RunAsync(root);
 m6Failed += await M6ReplayCliChecks.RunAsync(root);
 m6Failed += await M6ResourceChecks.RunAsync(root);
return failed == 0 && m3Failed == 0 && m4Failed == 0 && m5Failed == 0 && m6Failed == 0 ? 0 : 1;

string Fixture(string name) => Path.Combine(root, "tests", "Fixtures", "Core", name);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
}
static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AgentGame.slnx"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new Exception("Cannot locate solution.");
}
