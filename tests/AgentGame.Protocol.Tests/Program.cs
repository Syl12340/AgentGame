using System.Text;
using System.Text.Json;
using AgentGame.Protocol;

// Zero-NuGet console contract checks for AgentGame.Protocol. Returns exit code 0 iff every
// check passes; any failure prints to stderr and returns non-zero. It reads the fixture files
// under tests/Fixtures/Protocol and never touches game rules.

var tests = new (string Name, Action Run)[]
{
    ("Valid ready/action lines parse to typed responses", () =>
    {
        var lines = ReadLines(Root("tests", "Fixtures", "Protocol", "agent", "valid-agent-to-host.jsonl"));
        Equal<int>(5, lines.Length);
        var r0 = Require<ReadyResponse>(AgentResponseParser.Parse(lines[0]));
        Equal("agent/1", r0.Protocol); Equal("explorer", r0.Name);
        var a0 = Require<ActionResponse>(AgentResponseParser.Parse(lines[1]));
        Equal("r42", a0.RequestId); Equal(ActionTypeDto.Move, a0.Action.Type); Equal(DirectionDto.East, a0.Action.Direction);
        var a1 = Require<ActionResponse>(AgentResponseParser.Parse(lines[2]));
        Equal(ActionTypeDto.Interact, a1.Action.Type); Equal(DirectionDto.North, a1.Action.Direction);
        var a2 = Require<ActionResponse>(AgentResponseParser.Parse(lines[3]));
        Equal(ActionTypeDto.Pickup, a2.Action.Type); Check(a2.Action.Direction is null, "pickup must be directionless");
        var a3 = Require<ActionResponse>(AgentResponseParser.Parse(lines[4]));
        Equal(ActionTypeDto.Wait, a3.Action.Type); Check(a3.Action.Direction is null, "wait must be directionless");
    }),

    ("Every invalid-case fixture message is rejected", () =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Root("tests", "Fixtures", "Protocol", "agent", "invalid-cases.json")));
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            string line = prop.Value.ValueKind == JsonValueKind.Object
                ? prop.Value.GetRawText()
                : prop.Value.ValueKind == JsonValueKind.Null ? "null" : prop.Value.GetRawText();
            try { _ = AgentResponseParser.Parse(line); throw new Exception($"'{prop.Name}' was accepted."); }
            catch (ProtocolException) { }
        }
    }),

    ("Empty line and plain prose are rejected", () =>
    {
        Throws<ProtocolException>(() => AgentResponseParser.Parse(""));
        Throws<ProtocolException>(() => AgentResponseParser.Parse("\n"));
        Throws<ProtocolException>(() => AgentResponseParser.Parse("   "));
        Throws<ProtocolException>(() => AgentResponseParser.Parse("please move east now"));
        Throws<ProtocolException>(() => AgentResponseParser.Parse("Here you go: {\"type\":\"action\"}"));
    }),

    ("Markdown fences and extra JSON content are rejected", () =>
    {
        Throws<ProtocolException>(() => AgentResponseParser.Parse("{\"type\":\"action\",\"request_id\":\"r1\",\"action\":{\"type\":\"wait\"}} trailing"));
        Throws<ProtocolException>(() => AgentResponseParser.Parse("```json\n{\"type\":\"action\",\"request_id\":\"r1\",\"action\":{\"type\":\"wait\"}}\n```"));
        Throws<ProtocolException>(() => AgentResponseParser.Parse("{\"type\":\"action\",\"request_id\":\"r1\",\"action\":{\"type\":\"wait\"}} {\"type\":\"ready\"}"));
        Throws<ProtocolException>(() => AgentResponseParser.Parse(" {")); // leading whitespace
    }),

    ("Invalid UTF-8 and a UTF-8 BOM are rejected", () =>
    {
        byte[] bad = [0x7B, 0xFF, 0xFE, 0x7D];
        Throws<ProtocolException>(() => AgentResponseParser.Parse(bad));
        byte[] bom = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'];
        Throws<ProtocolException>(() => AgentResponseParser.Parse(bom));
    }),

    ("An over-length response line is rejected by byte count before parsing", () =>
    {
        string huge = "{\"type\":\"action\",\"request_id\":\"r\",\"action\":{\"type\":\"wait\"}}" +
            new string(' ', ProtocolLimits.MaxLineBytes + 1);
        Throws<ProtocolException>(() => AgentResponseParser.Parse(huge));
    }),

    ("JSON deeper than the depth limit is rejected", () =>
    {
        // A real deep test: a valid object whose action value nests arrays beyond depth 32.
        // (Root-level arrays were previously rejected for "not an object" before reaching the
        // depth limit, so a genuine depth violation only counts when nested inside the object.)
        string deep = "{\"type\":\"action\",\"request_id\":\"r1\",\"action\":" +
            string.Concat(Enumerable.Repeat("[", 40)) + "0" + string.Concat(Enumerable.Repeat("]", 40)) + "}";
        Throws<ProtocolException>(() => AgentResponseParser.Parse(deep));
        // A shallow-enough object with the same shape parses fine (guards the depth ceiling).
        string shallow = "{\"type\":\"action\",\"request_id\":\"r1\",\"action\":" +
            string.Concat(Enumerable.Repeat("[", 8)) + "0" + string.Concat(Enumerable.Repeat("]", 8)) + "}";
        using var _ = JsonDocument.Parse(shallow); // valid JSON, just not a valid action field
    }),

    ("Tab and backticks inside valid JSON string values are accepted", () =>
    {
        // Names may legitimately contain a tab or "```"; detection must be by JSON shape only.
        // A real tab in JSON source is escaped as "\t"; backticks and spaces are literal JSON.
        var ready = AgentResponseParser.Parse("{\"type\":\"ready\",\"protocol\":\"agent/1\",\"name\":\"tab\\t`agent```\"}");
        Equal("tab\t`agent```", Require<ReadyResponse>(ready).Name);
        var action = AgentResponseParser.Parse("{\"type\":\"action\",\"request_id\":\"r 1 `sx``\",\"action\":{\"type\":\"wait\"}}");
        Equal("r 1 `sx``", Require<ActionResponse>(action).RequestId);
    }),

    ("ParseReady/ParseAction phase and request_id validation", () =>
    {
        string readyLine = "{\"type\":\"ready\",\"protocol\":\"agent/1\",\"name\":\"explorer\"}";
        string actionLine = "{\"type\":\"action\",\"request_id\":\"r42\",\"action\":{\"type\":\"wait\"}}";
        var r0 = AgentResponseParser.ParseReady(readyLine);
        Equal("explorer", r0.Name);
        var a0 = AgentResponseParser.ParseAction(actionLine, "r42");
        Equal("r42", a0.RequestId);
        // Handshake phase: ready must not accept an action, action must not accept a ready.
        Throws<ProtocolException>(() => AgentResponseParser.ParseReady(actionLine));
        Throws<ProtocolException>(() => AgentResponseParser.ParseAction(readyLine, "r42"));
        // Wrong request_id is rejected.
        Throws<ProtocolException>(() => AgentResponseParser.ParseAction(actionLine, "r7"));
    }),

    ("Hello/observation/episode_end encode to the frozen agent/1 wire shape", () =>
    {
        Equal("{\"type\":\"hello\",\"protocol\":\"agent/1\",\"actions\":[\"move\",\"pickup\",\"interact\",\"wait\"]}",
            ProtocolJson.EncodeLine(new HelloMessage()));
        var obs = new ObservationMessage
        {
            RequestId = "r42", Tick = 42,
            Observation = new AgentObservationBody
            {
                Position = new PointDto { X = 8, Y = 5 },
                Tiles =
                [
                    new AgentTile { X = 8, Y = 5, Terrain = TerrainDto.Floor },
                    new AgentTile { X = 4, Y = 1, Terrain = TerrainDto.Floor, DoorOpen = false },
                ],
                Inventory = [ItemKindDto.Key],
                Mission = MissionPhaseDto.OpenDoor,
                LastResult = new ActionFeedback { Status = ActionStatusDto.Blocked, Reason = "closed_door" },
            },
        };
        Equal("{\"type\":\"observation\",\"request_id\":\"r42\",\"tick\":42,\"observation\":" +
            "{\"position\":{\"x\":8,\"y\":5}," +
            "\"tiles\":[{\"x\":8,\"y\":5,\"terrain\":\"floor\",\"is_exit\":false}," +
            "{\"x\":4,\"y\":1,\"terrain\":\"floor\",\"is_exit\":false,\"door_open\":false}]," +
            "\"inventory\":[\"key\"],\"mission\":\"open_door\"," +
            "\"last_result\":{\"status\":\"blocked\",\"reason\":\"closed_door\"}}}", ProtocolJson.EncodeLine(obs));
        var end = new EpisodeEndMessage
        {
            RequestId = "r42",
            Result = new EpisodeBody { Kind = EpisodeKindDto.Success },
            Observation = new AgentObservationBody { Position = new PointDto { X = 1, Y = 1 }, Mission = MissionPhaseDto.Succeeded },
        };
        Equal("{\"type\":\"episode_end\",\"request_id\":\"r42\",\"result\":{\"kind\":\"success\"}," +
            "\"observation\":{\"position\":{\"x\":1,\"y\":1},\"tiles\":[],\"inventory\":[],\"mission\":\"succeeded\"}}",
            ProtocolJson.EncodeLine(end));
    }),

    ("Host-to-agent fixture lines decode to the expected DTOs", () =>
    {
        var lines = ReadLines(Root("tests", "Fixtures", "Protocol", "agent", "valid-host-to-agent.jsonl"));
        Equal<int>(3, lines.Length);
        var hello = Deserialize<HelloMessage>(lines[0]);
        Equal("agent/1", hello.Protocol); Equal(4, hello.Actions.Length);
        var obs = Deserialize<ObservationMessage>(lines[1]);
        Equal("r42", obs.RequestId); Equal(42L, obs.Tick);
        Equal(2, obs.Observation.Tiles.Length); Equal(MissionPhaseDto.OpenDoor, obs.Observation.Mission);
        Equal(ActionStatusDto.Blocked, obs.Observation.LastResult!.Status);
        Equal("closed_door", obs.Observation.LastResult.Reason);
        var end = Deserialize<EpisodeEndMessage>(lines[2]);
        Equal(EpisodeKindDto.Success, end.Result.Kind);
        Equal(MissionPhaseDto.Succeeded, end.Observation.Mission);
    }),

    ("Observer encoding preserves explicit clearing values and patch field", () =>
    {
        var batch = new StepBatchMessage
        {
            RunId = "demo", Seq = 107, Tick = 43,
            Patch = new ObserverPatch
            {
                TileUpserts = [new ObserverTile { X = 2, Y = 1, Terrain = TerrainDto.Floor }],
                EntityRemovals = ["key_1"], VisibleTiles = [new PointDto { X = 2, Y = 1 }],
                Inventory = [ItemKindDto.Key], MissionPhase = MissionPhaseDto.OpenDoor
            }
        };
        using var document = JsonDocument.Parse(ProtocolJson.EncodeLine(batch));
        var root = document.RootElement;
        Check(!root.TryGetProperty("state", out _), "StepBatch must carry patch, not state.");
        var patch = root.GetProperty("patch");
        foreach (string field in new[] { "tile_upserts", "entity_upserts", "entity_removals", "visible_tiles", "inventory", "mission_phase", "episode" })
            Check(patch.TryGetProperty(field, out _), $"Missing mandatory patch field {field}.");
        Equal(JsonValueKind.Null, patch.GetProperty("episode").ValueKind);
        var tile = patch.GetProperty("tile_upserts")[0];
        foreach (string field in new[] { "item", "door_open", "last_seen_tick" })
            Equal(JsonValueKind.Null, tile.GetProperty(field).ValueKind);
        Equal("open_door", patch.GetProperty("mission_phase").GetString());
        Equal("key_1", patch.GetProperty("entity_removals")[0].GetString());
        var roundtrip = Deserialize<StepBatchMessage>(document.RootElement.GetRawText());
        Check(roundtrip.Patch.TileUpserts[0].Item is null && roundtrip.Patch.Episode is null, "Clearing values lost.");
    }),
    ("Observer snapshot and fixture use full state then explicit patch", () =>
    {
        var lines = ReadLines(Root("tests", "Fixtures", "Protocol", "observer", "valid-observer.jsonl"));
        Equal(2, lines.Length);
        var snap = Deserialize<SnapshotMessage>(lines[0]);
        var batch = Deserialize<StepBatchMessage>(lines[1]);
        Equal(snap.BaseSeq + 1, batch.Seq);
        Equal(MissionPhaseDto.FindCore, batch.Patch.MissionPhase);
        Equal("door_1", batch.Events[0].EntityId);
        Check(batch.Patch.TileUpserts.Any(t => t.DoorOpen == true && t.LastSeenTick is null), "Door update absent.");
        using var encoded = JsonDocument.Parse(ProtocolJson.EncodeLine(new SnapshotMessage()));
        Equal(JsonValueKind.Null, encoded.RootElement.GetProperty("state").GetProperty("episode").ValueKind);
    }),
    ("JSONL allows legal horizontal whitespace but rejects multiple physical lines", () =>
    {
        Equal("x", AgentResponseParser.ParseReady(" \t{\"type\": \"ready\", \"protocol\":\"agent/1\", \"name\":\"x\"} \t").Name);
        _ = AgentResponseParser.ParseAction("{\"type\":\"action\",\"request_id\":\"r1\",\"action\":{\"type\":\"wait\"}}\r\n", "r1");
        Throws<ProtocolException>(() => AgentResponseParser.ParseReady("{\n\"type\":\"ready\",\"protocol\":\"agent/1\",\"name\":\"x\"}"));
        Throws<ProtocolException>(() => AgentResponseParser.ParseReady("{\"type\":\"ready\",\"protocol\":\"agent/1\",\"name\":\"x\"}\n\n"));
        Throws<ProtocolException>(() => AgentResponseParser.ParseReady("{\"type\":\"ready\",\"protocol\":\"agent/1\",\"name\":\"" + new string((char)0xD800, 1) + "\"}"));
    }),
    ("Hello instances cannot mutate the advertised action specification", () =>
    {
        var a = new HelloMessage(); a.Actions[0] = "invalid";
        Equal("move", new HelloMessage().Actions[0]);
        var advertised = ProtocolLimits.HelloActions; advertised[0] = "invalid";
        Equal("move", ProtocolLimits.HelloActions[0]);
        Check(ProtocolJson.Options.IsReadOnly, "Shared JSON options must be frozen.");
    }),
    ("Scenario metadata preserves UInt64 seeds without JSON number precision loss", () =>
    {
        var scene = ScenarioCodec.Parse(File.ReadAllBytes(Root("tests", "Fixtures", "Protocol", "scenario", "facility-small.json"))) with
        { Seed = "18446744073709551615", GenerationAttempts = 2, ReferenceLength = 13 };
        var roundtrip = ScenarioCodec.Parse(ScenarioCodec.Encode(scene));
        Equal(scene.Seed, roundtrip.Seed); Equal(2, roundtrip.GenerationAttempts); Equal(13, roundtrip.ReferenceLength);
        foreach (string seed in new[] { "01", "-1", "18446744073709551616", "1.5" })
            Throws<ProtocolException>(() => ScenarioCodec.Parse(ScenarioCodec.Encode(scene with { Seed = seed })));
        Throws<ProtocolException>(() => ScenarioCodec.Parse(ScenarioCodec.Encode(scene with { ReferenceLength = 513 })));
    }),

    ("Scenario valid fixtures parse and encode to canonical scenario/1", () =>
    {
        var s1 = ScenarioCodec.Parse(File.ReadAllBytes(Root("tests", "Fixtures", "Protocol", "scenario", "facility-small.json")));
        Equal("scenario/1", s1.Format); Equal("facility-zero/1", s1.Rules); Equal("manual/1", s1.Generator);
        Equal(9, s1.Rows[0].Length); Equal(3, s1.Rows.Length);
        Equal((1, 1), (s1.Start.X, s1.Start.Y)); Equal((4, 1), (s1.Door.X, s1.Door.Y));
        Equal(512, s1.MaxTicks); Equal(3, s1.VisibilityRadius);
        Equal("{\"format\":\"scenario/1\",\"rules\":\"facility-zero/1\",\"generator\":\"manual/1\"," +
            "\"rows\":[\"#########\",\"#.......#\",\"#########\"]," +
            "\"start\":{\"x\":1,\"y\":1},\"exit\":{\"x\":1,\"y\":1},\"key\":{\"x\":2,\"y\":1}," +
            "\"door\":{\"x\":4,\"y\":1},\"core\":{\"x\":6,\"y\":1},\"max_ticks\":512,\"visibility_radius\":3}",
            ScenarioCodec.Encode(s1));

        var s2 = ScenarioCodec.Parse(File.ReadAllBytes(Root("tests", "Fixtures", "Protocol", "scenario", "facility-tall.json")));
        Equal(7, s2.Rows[0].Length); Equal(2, s2.Rows.Length); Equal(64, s2.MaxTicks);
    }),

    ("Invalid scenario fixtures are rejected", () =>
    {
        foreach ((string file, string? contains) in new (string, string?)[]
        {
            ( Root("tests","Fixtures","Protocol","scenario","invalid-extra-field.json"), "unknown field" ),
            ( Root("tests","Fixtures","Protocol","scenario","invalid-bad-cell.json"), "illegal character" ),
            ( Root("tests","Fixtures","Protocol","scenario","invalid-overlap-and-budget.json"), null ),
        })
        {
            try { _ = ScenarioCodec.Parse(File.ReadAllBytes(file)); throw new Exception($"'{file}' was accepted."); }
            catch (ProtocolException e)
            {
                if (contains is not null) Check(e.Message.Contains(contains, StringComparison.Ordinal), $"Unexpected message: {e.Message}");
            }
        }
    }),

    ("Scenario rejects invalid UTF-8 and duplicate nested coordinates", () =>
    {
        byte[] bytes = File.ReadAllBytes(Root("tests", "Fixtures", "Protocol", "scenario", "facility-small.json"));
        int index = Encoding.UTF8.GetString(bytes).IndexOf("manual/1", StringComparison.Ordinal);
        bytes[index] = 0xFF;
        Throws<ProtocolException>(() => ScenarioCodec.Parse(bytes));
        var scene = ScenarioCodec.Parse(File.ReadAllBytes(Root("tests", "Fixtures", "Protocol", "scenario", "facility-small.json")));
        string duplicate = ScenarioCodec.Encode(scene).Replace("\"start\":{\"x\":1", "\"start\":{\"x\":1,\"x\":1", StringComparison.Ordinal);
        Throws<ProtocolException>(() => ScenarioCodec.Parse(duplicate));
    }),
    ("Scenario round-trip preserves all frozen fields", () =>
    {
        var path = Root("tests", "Fixtures", "Protocol", "scenario", "facility-small.json");
        var originalBytes = File.ReadAllBytes(path);
        var scenario = ScenarioCodec.Parse(originalBytes);
        string canonical = ScenarioCodec.Encode(scenario);
        var roundTripped = ScenarioCodec.Parse(Encoding.UTF8.GetBytes(canonical));
        using var a = JsonDocument.Parse(originalBytes);
        using var b = JsonDocument.Parse(Encoding.UTF8.GetBytes(canonical));
        EqualJson(a.RootElement, b.RootElement, "scenario round-trip");
        Equal(scenario.Rows.Length, roundTripped.Rows.Length);
        Equal(scenario.MaxTicks, roundTripped.MaxTicks);
        Equal(scenario.Start.X, roundTripped.Start.X);
    }),

    ("Replay fixture preserves scenario, actions, hash and all observer seq", () =>
    {
        var lines = ReadLines(Root("tests", "Fixtures", "Protocol", "replay", "valid-replay.jsonl"));
        Equal<int>(4, lines.Length);
        var header = Deserialize<ReplayRunHeader>(lines[0]);
        Equal("replay/1", header.Replay); Equal("facility-zero/1", header.Rules);
        Equal(9, header.Scenario.Rows[0].Length); Equal(0L, header.InitialSnapshot.BaseSeq);
        // The status envelope is a full observer envelope and its seq is continuous after the
        // snapshot's base_seq (first post-snapshot envelope = base_seq + 1), so nothing is lost.
        var status = Deserialize<ReplayStatusRecord>(lines[1]);
        Equal(1L, status.ObserverStatus.Seq); Equal(AgentStatusDto.Waiting, status.ObserverStatus.AgentStatus);
        Equal("observer/1", status.ObserverStatus.Protocol); Equal("demo", status.ObserverStatus.RunId); Equal("agent", status.ObserverStatus.View);
        var step = Deserialize<ReplayStepRecord>(lines[2]);
        Equal(2L, step.Seq); Equal(1L, step.Tick); Equal(ActionTypeDto.Move, step.Action.Type); Equal(DirectionDto.East, step.Action.Direction);
        Equal(step.Seq, step.ObserverBatch.Seq);
        Equal("core-state/1", header.CoreEncoding);
        using var golden = JsonDocument.Parse(File.ReadAllText(Root("tests", "Fixtures", "Core", "core-golden.json")));
        Equal(golden.RootElement.GetProperty("first_move_sha256").GetString(), step.CoreHash);
        Equal(64, step.CoreHash.Length); // hex sha256
        Equal(2L, step.ObserverBatch.Seq);
        Equal(ActionStatusDto.Applied, step.Outcome.Status);
        var footer = Deserialize<ReplayRunFooter>(lines[3]);
        Equal("aborted", footer.Status); Check(footer.Result is null, "aborted replay must carry no result");
        Equal(1L, footer.LastTick);
    }),

    ("Encoding a committed action keeps the canonical ActionRequestDto shape", () =>
    {
        var action = new ActionRequestDto { Type = ActionTypeDto.Interact, Direction = DirectionDto.West };
        Equal("{\"type\":\"interact\",\"direction\":\"west\"}", ProtocolJson.EncodeLine(action));
        var pickup = new ActionRequestDto { Type = ActionTypeDto.Wait };
        Equal("{\"type\":\"wait\"}", ProtocolJson.EncodeLine(pickup));
    }),
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
Console.WriteLine($"Protocol: {tests.Length - failed}/{tests.Length} passed.");
failed += M4CodecChecks.Run(Root());
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
}
static void Throws<T>(Action operation) where T : Exception
{
    try { operation(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static T Require<T>(AgentResponse response) where T : AgentResponse
    => response is T typed ? typed : throw new Exception($"Expected {typeof(T).Name}, got {response.GetType().Name}.");
static string Root(params string[] parts)
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "docs", "parallel-work.md"))) break;
        directory = directory.Parent;
    }
    if (directory is null) throw new Exception("Cannot locate fixture root.");
    List<string> all = [directory.FullName, .. parts];
    return Path.Combine(all.ToArray());
}
static string[] ReadLines(string path) => File.ReadAllLines(path);
static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, ProtocolJson.Options)
    ?? throw new Exception($"Deserialize returned null for {typeof(T).Name}.");
static void EqualJson(JsonElement expected, JsonElement actual, string context)
{
    static string Canon(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            return "{" + string.Join(",", names.Select(n => $"\"{n}\":{Canon(e.GetProperty(n))}")) + "}";
        }
        if (e.ValueKind == JsonValueKind.Array)
            return "[" + string.Join(",", e.EnumerateArray().Select(Canon)) + "]";
        return e.GetRawText();
    }
    if (Canon(expected) != Canon(actual)) throw new Exception($"{context}: JSON mismatch.");
}
