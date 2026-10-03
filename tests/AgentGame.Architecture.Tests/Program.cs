using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

string root = FindRoot();
var checks = new List<(string Name, Action Run)>();
var projects = new (string Path, string[] References)[]
{
    ("src/AgentGame.Core/AgentGame.Core.csproj", []),
    ("src/AgentGame.Protocol/AgentGame.Protocol.csproj", []),
    ("src/AgentGame.Runtime/AgentGame.Runtime.csproj", ["AgentGame.Core", "AgentGame.Protocol"]),
    ("src/AgentGame.Cli/AgentGame.Cli.csproj", ["AgentGame.Runtime", "AgentGame.Protocol"]),
    ("tests/AgentGame.Core.Tests/AgentGame.Core.Tests.csproj", ["AgentGame.Core"]),
    ("tests/AgentGame.Protocol.Tests/AgentGame.Protocol.Tests.csproj", ["AgentGame.Protocol"]),
    ("tests/AgentGame.Runtime.Tests/AgentGame.Runtime.Tests.csproj", ["AgentGame.Runtime", "AgentGame.Protocol"]),
    ("tests/AgentGame.Architecture.Tests/AgentGame.Architecture.Tests.csproj", [])
};
foreach (var (path, expected) in projects)
{
    checks.Add(($"Project references: {path}", () =>
    {
        var xml = XDocument.Load(Path.Combine(root, path));
        var actual = xml.Descendants("ProjectReference").Select(e =>
            Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/'))).Order(StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(expected.Order(StringComparer.Ordinal)), $"Unexpected references: {string.Join(',', actual)}.");
        Require(!xml.Descendants("PackageReference").Any(), "Unexpected package at this zero-dependency milestone.");
    }));
}
checks.Add(("SDK and compiler configuration", () =>
{
    var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
    Require(props.Descendants("TargetFramework").Single().Value == "net10.0", "Framework changed.");
    Require(props.Descendants("Nullable").Single().Value == "enable", "Nullable disabled.");
    Require(props.Descendants("TreatWarningsAsErrors").Single().Value == "true", "Compiler warnings not enforced.");
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
    Require(json.RootElement.GetProperty("sdk").GetProperty("version").GetString() == "10.0.401", "SDK not pinned.");
    Require(json.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString() == "disable", "SDK floats.");
}));
checks.Add(("Core source has no I/O, JSON, wall clock or terminal dependencies", () =>
{
    string pattern = @"System\.(IO|Diagnostics|Text\.Json|Threading\.Channels)|\b(DateTime|DateTimeOffset|Stopwatch|Console|Process|Channel)\b|Spectre|AgentGame\.(Protocol|Runtime|Cli)";
    CheckSource("src/AgentGame.Core", pattern);
}));
checks.Add(("Protocol source does not reference Core", () => CheckSource("src/AgentGame.Protocol", @"AgentGame\.(Core|Runtime|Cli)")));
checks.Add(("Cli source does not access Core", () => CheckSource("src/AgentGame.Cli", @"AgentGame\.Core")));
int failed = 0;
foreach (var (name, check) in checks)
{
    try { check(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
Console.WriteLine($"Architecture: {checks.Count - failed}/{checks.Count} passed.");
return failed == 0 ? 0 : 1;

void CheckSource(string folder, string pattern)
{
    var files = Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal) &&
                       !file.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)).ToArray();
    Require(files.Length > 0, "Expected source files.");
    foreach (string file in files)
        Require(!Regex.IsMatch(File.ReadAllText(file), pattern), $"Forbidden dependency in {Path.GetRelativePath(root, file)}.");
}
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AgentGame.slnx"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new Exception("Cannot locate solution root.");
}
