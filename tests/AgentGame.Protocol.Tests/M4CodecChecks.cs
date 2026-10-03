using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGame.Protocol;

internal static class M4CodecChecks
{
    public static int Run(string root)
    {
        SnapshotMessage snapshot = new()
        {
            RunId = "codec-test", AgentStatus = AgentStatusDto.Waiting,
            State = new ObserverState
            {
                VisibleRadius = 3, Tiles = [new ObserverTile { X = 1, Y = 1, Terrain = TerrainDto.Floor }],
                Entities = [new ObserverEntity { Id = "agent", Kind = "agent", X = 1, Y = 1 }],
            },
        };
        StepBatchMessage batch = new()
        {
            RunId = "codec-test", Seq = 2, Tick = 1, AgentStatus = AgentStatusDto.ActionReceived,
            Events = [new ObserverEventDto { Type = ObserverEventTypeDto.Waited }],
            Patch = new ObserverPatch
            {
                TileUpserts = [new ObserverTile { X = 1, Y = 1, Terrain = TerrainDto.Floor }],
                EntityUpserts = [new ObserverEntity { Id = "agent", Kind = "agent", X = 1, Y = 1 }],
                VisibleTiles = [new PointDto { X = 1, Y = 1 }],
            },
        };
        AgentStatusMessage status = new() { RunId = "codec-test", Seq = 1, AgentStatus = AgentStatusDto.Waiting };
        ReplayStepRecord step = new()
        {
            Seq = 2, Tick = 1, Action = new ActionRequestDto { Type = ActionTypeDto.Wait },
            Outcome = new ActionFeedback { Status = ActionStatusDto.Applied }, CoreHash = new string('a', 64), ObserverBatch = batch,
        };
        ScenarioDto scenario = ScenarioCodec.Parse(File.ReadAllBytes(Path.Combine(root, "tests", "Fixtures", "Protocol", "scenario", "facility-small.json")));
        ReplayRunHeader header = new() { Scenario = scenario, InitialSnapshot = snapshot, AgentName = "" };
        string snapJson = ObserverCodec.Encode(snapshot), batchJson = ObserverCodec.Encode(batch), statusJson = ObserverCodec.Encode(status);
        string stepJson = ReplayCodec.Encode(step), headerJson = ReplayCodec.Encode(header);

        (string Name, Action Check)[] checks =
        [
            ("Observer strict codecs round-trip all envelope types and explicit clearing values", () =>
            {
                foreach (object message in new object[] { snapshot, batch, status })
                    Equal(ObserverCodec.Encode(message), ObserverCodec.Encode(ObserverCodec.Parse(ObserverCodec.Encode(message))));
                var parsed = (StepBatchMessage)ObserverCodec.Parse(batchJson);
                Check(parsed.Patch.Episode is null && parsed.Patch.TileUpserts[0].Item is null, "Explicit null values changed.");
                Check(ObserverCodec.Parse("\t " + snapJson + " \r\n") is SnapshotMessage, "Legal JSONL whitespace rejected.");
            }),
            ("Replay strict codecs round-trip every record type", () =>
            {
                foreach (object record in new object[] { header, new ReplayStatusRecord { ObserverStatus = status }, step, new ReplayRunFooter { Status = "aborted", LastTick = 1 } })
                    Equal(ReplayCodec.Encode(record), ReplayCodec.Encode(ReplayCodec.Parse(ReplayCodec.Encode(record))));
                foreach (string line in File.ReadAllLines(Path.Combine(root, "tests", "Fixtures", "Protocol", "replay", "valid-replay.jsonl")))
                    _ = ReplayCodec.Parse(line);
            }),
            ("Observer rejects every missing top-level and complete state field", () =>
            {
                foreach (string field in new[] { "type", "protocol", "run_id", "view", "base_seq", "tick", "agent_status", "state" })
                    RejectObserver(Edit(snapJson, node => node.Remove(field)));
                foreach (string field in new[] { "visible_radius", "tiles", "entities", "inventory", "mission_phase", "episode" })
                    RejectObserver(Edit(snapJson, node => node["state"]!.AsObject().Remove(field)));
            }),
            ("Observer rejects every missing patch and full tile field", () =>
            {
                foreach (string field in new[] { "tile_upserts", "entity_upserts", "entity_removals", "visible_tiles", "inventory", "mission_phase", "episode" })
                    RejectObserver(Edit(batchJson, node => node["patch"]!.AsObject().Remove(field)));
                foreach (string field in new[] { "x", "y", "terrain", "item", "is_exit", "door_open", "last_seen_tick" })
                    RejectObserver(Edit(batchJson, node => node["patch"]!["tile_upserts"]![0]!.AsObject().Remove(field)));
            }),
            ("Observer rejects nested nulls and unknown fields instead of DTO defaults", () =>
            {
                foreach (string field in new[] { "tile_upserts", "entity_upserts", "entity_removals", "visible_tiles", "inventory", "mission_phase" })
                    RejectObserver(Edit(batchJson, node => node["patch"]![field] = null));
                RejectObserver(Edit(batchJson, node => node["patch"]!["extra"] = 1));
                RejectObserver(Edit(batchJson, node => node["events"]![0]!["type"] = null));
                RejectObserver(Edit(snapJson, node => node["state"]!["entities"]![0]!["kind"] = null));
                RejectObserver(Edit(batchJson, node => node["patch"]!["visible_tiles"]![0]!.AsObject().Remove("y")));
            }),
            ("Observer rejects duplicate keys at envelope and nested levels", () =>
            {
                RejectObserver(statusJson.Replace("\"seq\":1", "\"seq\":1,\"seq\":1", StringComparison.Ordinal));
                RejectObserver(batchJson.Replace("\"x\":1", "\"x\":1,\"x\":1", StringComparison.Ordinal));
                RejectReplay("{\"type\":\"run_footer\",\"status\":\"aborted\",\"result\":null,\"last_tick\":0,\"stats\":{\"x\":1,\"x\":2}}");
            }),
            ("Observer rejects duplicate collections and conflicting entity patches", () =>
            {
                RejectObserver(Edit(batchJson, node => Duplicate(node["patch"]!["tile_upserts"]!.AsArray())));
                RejectObserver(Edit(batchJson, node => Duplicate(node["patch"]!["entity_upserts"]!.AsArray())));
                RejectObserver(Edit(batchJson, node => Duplicate(node["patch"]!["visible_tiles"]!.AsArray())));
                RejectObserver(Edit(batchJson, node => node["patch"]!["entity_removals"] = new JsonArray("agent")));
                RejectObserver(Edit(batchJson, node => node["patch"]!["entity_removals"] = new JsonArray("gone", "gone")));
                RejectObserver(Edit(snapJson, node => node["state"]!["inventory"] = new JsonArray("key", "key")));
            }),
            ("Observer rejects inconsistent tile visibility and future history", () =>
            {
                RejectObserver(Edit(batchJson, node => node["patch"]!["tile_upserts"]![0]!["last_seen_tick"] = 0));
                RejectObserver(Edit(batchJson, node => node["patch"]!["visible_tiles"] = new JsonArray()));
                RejectObserver(Edit(snapJson, node => node["state"]!["tiles"]![0]!["last_seen_tick"] = 1));
                RejectObserver(Edit(snapJson, node => node["state"]!["entities"]![0]!["x"] = 2));
                RejectObserver(Edit(snapJson, node => { node["state"]!["tiles"]![0]!["terrain"] = "wall"; node["state"]!["tiles"]![0]!["is_exit"] = true; }));
            }),
            ("Observer preserves memory entities and signed attempted event coordinates", () =>
            {
                _ = ObserverCodec.Parse(Edit(snapJson, node => { node["tick"] = 1; node["state"]!["tiles"]![0]!["last_seen_tick"] = 0; }));
                _ = ObserverCodec.Parse(Edit(batchJson, node => node["patch"]!["entity_upserts"]![0]!["x"] = 2));
                foreach (int x in new[] { -1, 128, int.MinValue, int.MaxValue })
                    _ = ObserverCodec.Parse(Edit(batchJson, node => node["events"]![0]!["position"] = new JsonObject { ["x"] = x, ["y"] = -1 }));
                RejectObserver(Edit(batchJson, node => node["events"]![0]!["position"] = new JsonObject { ["x"] = (long)int.MaxValue + 1, ["y"] = 0 }));
            }),
            ("Observer enum values are exact strings, never numbers or aliases", () =>
            {
                foreach (JsonNode? invalid in new JsonNode?[] { JsonValue.Create(0), JsonValue.Create("Waiting"), JsonValue.Create("missing"), null })
                    RejectObserver(Edit(statusJson, node => node["agent_status"] = invalid?.DeepClone()));
                RejectObserver(Edit(batchJson, node => node["events"]![0]!["type"] = "Waited"));
                RejectObserver(Edit(batchJson, node => node["patch"]!["mission_phase"] = 0));
                RejectObserver(Edit(batchJson, node => node["patch"]!["tile_upserts"]![0]!["item"] = 1));
            }),
            ("Observer seq and tick enforce JSON safe nonnegative integers", () =>
            {
                foreach (long number in new long[] { -1, 9_007_199_254_740_992, long.MaxValue })
                {
                    RejectObserver(Edit(statusJson, node => node["seq"] = number));
                    RejectObserver(Edit(statusJson, node => node["tick"] = number));
                    RejectObserver(Edit(snapJson, node => node["base_seq"] = number));
                }
                _ = ObserverCodec.Parse(Edit(statusJson, node => node["seq"] = 9_007_199_254_740_991L));
                RejectObserver(Edit(statusJson, node => node["seq"] = 0.5));
                RejectObserver(Edit(batchJson, node => node["patch"]!["visible_tiles"]![0]!["x"] = -1));
            }),
            ("Observer identity and wire version cannot be missing or unsupported", () =>
            {
                foreach (string field in new[] { "protocol", "run_id", "view" })
                {
                    RejectObserver(Edit(statusJson, node => node[field] = ""));
                    RejectObserver(Edit(statusJson, node => node.Remove(field)));
                }
                RejectObserver(Edit(statusJson, node => node["view"] = "full_map"));
                RejectObserver(Edit(statusJson, node => node["protocol"] = "observer/2"));
            }),
            ("M4 codecs reject multiline, extra content, BOM and invalid UTF-8", () =>
            {
                foreach (string invalid in new[] { "[]", snapJson + " {}", snapJson + "\n\n", snapJson + "\r", snapJson.Replace("\"state\":", "\n\"state\":", StringComparison.Ordinal), "```json\n" + snapJson + "\n```" })
                    RejectObserver(invalid);
                Throws(() => ObserverCodec.Parse(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(snapJson)).ToArray()));
                Throws(() => ReplayCodec.Parse(new byte[] { (byte)'{', 0xff, (byte)'}' }));
                Throws(() => ObserverCodec.Parse(new byte[] { (byte)'{', 0xc0, 0xaf, (byte)'}' }));
            }),
            ("M4 codecs reject malformed escaped Unicode and excessive JSON depth", () =>
            {
                RejectObserver(statusJson.Replace("codec-test", "\\uD800", StringComparison.Ordinal));
                RejectObserver(statusJson.Replace("codec-test", "\\uDC00", StringComparison.Ordinal));
                RejectObserver(statusJson.Replace("codec-test", new string((char)0xd800, 1), StringComparison.Ordinal));
                RejectReplay("{\"type\":\"run_footer\",\"status\":\"aborted\",\"result\":null,\"last_tick\":0,\"stats\":" + new string('[', 40) + "0" + new string(']', 40) + "}");
                _ = ObserverCodec.Parse(statusJson.Replace("codec-test", "\\uD83D\\uDE00", StringComparison.Ordinal));
            }),
            ("M4 codec line limits count UTF-8 bytes and permit one terminator", () =>
            {
                RejectObserver(statusJson + new string(' ', ObserverCodec.MaxMessageBytes));
                RejectReplay(stepJson + new string(' ', ReplayCodec.MaxRecordBytes));
                string boundary = statusJson + new string(' ', ObserverCodec.MaxMessageBytes - Encoding.UTF8.GetByteCount(statusJson));
                _ = ObserverCodec.Parse(boundary + "\r\n");
                RejectObserver(statusJson.Replace("codec-test", new string('界', ObserverCodec.MaxMessageBytes / 2), StringComparison.Ordinal));
            }),
            ("Replay required metadata and nested status envelope fields cannot default", () =>
            {
                foreach (string field in new[] { "type", "replay", "agent_protocol", "observer_protocol", "rules", "core_encoding", "generator", "agent_name", "scenario", "initial_snapshot" })
                    RejectReplay(Edit(headerJson, node => node.Remove(field)));
                foreach (string field in new[] { "type", "protocol", "run_id", "view", "seq", "tick", "agent_status" })
                    RejectReplay(Edit(ReplayCodec.Encode(new ReplayStatusRecord { ObserverStatus = status }), node => node["observer_status"]!.AsObject().Remove(field)));
                RejectReplay(Edit(headerJson, node => node["replay"] = "replay/2"));
                RejectReplay(Edit(headerJson, node => node["observer_protocol"] = "observer/2"));
                RejectReplay(Edit(headerJson, node => node["initial_snapshot"]!["base_seq"] = 1));
                RejectReplay(Edit(headerJson, node => node["initial_snapshot"]!["tick"] = 1));
            }),
            ("Replay playback preserves unknown rule and core encoding metadata with structural checks", () =>
            {
                string future = Edit(headerJson, node =>
                {
                    node["rules"] = "facility-future/42"; node["scenario"]!["rules"] = "facility-future/42";
                    node["core_encoding"] = "core-state/42"; node["agent_protocol"] = "agent/42";
                });
                var parsed = (ReplayRunHeader)ReplayCodec.Parse(future);
                Equal("facility-future/42", parsed.Scenario.Rules); Equal("core-state/42", parsed.CoreEncoding);
                _ = ReplayCodec.Parse(ReplayCodec.Encode(parsed));
                Throws(() => ScenarioCodec.Parse(ScenarioCodec.Encode(parsed.Scenario)));
                RejectReplay(Edit(future, node => node["scenario"]!["rules"] = "mismatched/1"));
                RejectReplay(Edit(future, node => node["scenario"]!["start"]!["x"] = -1));
                RejectReplay(Edit(future, node => node["scenario"]!["extra"] = true));
                RejectReplay(Edit(future, node => node["scenario"]!["rows"] = new JsonArray("..", "...")));
            }),
            ("Replay steps reject mismatched envelope identity numbers and invalid hashes", () =>
            {
                RejectReplay(Edit(stepJson, node => node["seq"] = 3));
                RejectReplay(Edit(stepJson, node => node["tick"] = 2));
                RejectReplay(Edit(stepJson, node => node["seq"] = 9_007_199_254_740_992L));
                foreach (string hash in new[] { "", new string('A', 64), new string('z', 64), new string('a', 63) })
                    RejectReplay(Edit(stepJson, node => node["core_hash"] = hash));
                RejectReplay(Edit(stepJson, node => node["view_hash"] = null));
                RejectReplay(Edit(stepJson, node => node["outcome"]!.AsObject().Remove("status")));
                RejectReplay(Edit(stepJson, node => node["observer_batch"]!.AsObject().Remove("events")));
            }),
            ("Replay canonical action shapes require directions only for move and interact", () =>
            {
                foreach (string type in new[] { "move", "interact" })
                {
                    RejectReplay(Edit(stepJson, node => node["action"]!["type"] = type));
                    _ = ReplayCodec.Parse(Edit(stepJson, node => { node["action"]!["type"] = type; node["action"]!["direction"] = "north"; }));
                }
                foreach (string type in new[] { "pickup", "wait" })
                    RejectReplay(Edit(stepJson, node => { node["action"]!["type"] = type; node["action"]!["direction"] = "north"; }));
                RejectReplay(Edit(stepJson, node => node["action"]!["direction"] = null));
                RejectReplay(Edit(stepJson, node => node["action"]!["extra"] = true));
            }),
            ("Replay footer requires explicit result and bounded tick but permits arbitrary stats", () =>
            {
                string footer = ReplayCodec.Encode(new ReplayRunFooter { Status = "aborted" });
                foreach (string field in new[] { "type", "status", "result", "last_tick" })
                    RejectReplay(Edit(footer, node => node.Remove(field)));
                RejectReplay(Edit(footer, node => node["status"] = "incomplete"));
                RejectReplay(Edit(footer, node => node["last_tick"] = -1));
                RejectReplay(Edit(footer, node => node["result"] = new JsonObject { ["kind"] = "error" }));
                _ = ReplayCodec.Parse(Edit(footer, node => node["stats"] = new JsonArray(1, "text", true, null)));
            }),
            ("M4 codecs round-trip a complete 128x128 map snapshot and replay header", () =>
            {
                ObserverTile[] tiles = Enumerable.Range(0, 128 * 128).Select(index => new ObserverTile
                {
                    X = index % 128, Y = index / 128, Terrain = TerrainDto.Floor,
                    IsExit = index == 0,
                    Item = index == 1 ? ItemKindDto.Key : index == 3 ? ItemKindDto.Core : null,
                    DoorOpen = index == 2 ? false : null,
                }).ToArray();
                SnapshotMessage fullSnapshot = snapshot with
                {
                    State = snapshot.State with
                    {
                        VisibleRadius = 128, Tiles = tiles,
                        Entities = [new ObserverEntity { Id = "agent", Kind = "agent", X = 0, Y = 0 }],
                    },
                };
                ScenarioDto fullScenario = scenario with
                {
                    Rows = Enumerable.Repeat(new string('.', 128), 128).ToArray(), VisibilityRadius = 128,
                    Start = new PointDto { X = 0, Y = 0 }, Exit = new PointDto { X = 0, Y = 0 },
                    Key = new PointDto { X = 1, Y = 0 }, Door = new PointDto { X = 2, Y = 0 }, Core = new PointDto { X = 3, Y = 0 },
                };
                string fullSnapshotJson = ObserverCodec.Encode(fullSnapshot);
                Check(Encoding.UTF8.GetByteCount(fullSnapshotJson) > ProtocolLimits.MaxLineBytes, "Maximum-map check did not exceed the Agent line limit.");
                var parsedSnapshot = (SnapshotMessage)ObserverCodec.Parse(fullSnapshotJson);
                Equal(16_384, parsedSnapshot.State.Tiles.Length);
                Equal(fullSnapshotJson, ObserverCodec.Encode(parsedSnapshot));
                string fullHeaderJson = ReplayCodec.Encode(header with { Scenario = fullScenario, InitialSnapshot = fullSnapshot });
                var parsedHeader = (ReplayRunHeader)ReplayCodec.Parse(fullHeaderJson);
                Equal(128, parsedHeader.Scenario.Rows.Length);
                Equal(16_384, parsedHeader.InitialSnapshot.State.Tiles.Length);
                Equal(fullHeaderJson, ReplayCodec.Encode(parsedHeader));
            }),
            ("M4 encoders reject unsupported carriers and invalid DTO values", () =>
            {
                Throws(() => ObserverCodec.Encode(new HelloMessage()));
                Throws(() => ReplayCodec.Encode(snapshot));
                Throws(() => ObserverCodec.Encode(status with { Seq = -1 }));
                Throws(() => ObserverCodec.Encode(status with { AgentStatus = (AgentStatusDto)99 }));
                Throws(() => ReplayCodec.Encode(step with { CoreHash = "invalid" }));
                Throws(() => ReplayCodec.Encode(step with { ObserverBatch = batch with { Tick = 2 } }));
            }),
        ];
        int failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
        }
        Console.WriteLine($"M4 codecs: {checks.Length - failures}/{checks.Length} passed.");
        return failures;
    }

    private static string Edit(string json, Action<JsonObject> edit)
    {
        JsonObject node = JsonNode.Parse(json)!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static void Duplicate(JsonArray array) => array.Add(array[0]!.DeepClone());
    private static void RejectObserver(string json) => Throws(() => ObserverCodec.Parse(json));
    private static void RejectReplay(string json) => Throws(() => ReplayCodec.Parse(json));
    private static void Throws(Action check)
    {
        try { check(); } catch (ProtocolException) { return; }
        throw new Exception("Expected ProtocolException.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
