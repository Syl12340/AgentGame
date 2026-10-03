using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGame.Protocol;

// M7 Protocol v2 codec checks. These exercise the scenario/2, agent/2, observer/2, replay/2
// codecs and verify the v1 (scenario/1, agent/1, observer/1, replay/1) codecs and fixtures are
// unchanged.

internal static class M7ProtocolV2Checks
{
    public static int Run(string root)
    {
        (string Name, Action Check)[] checks =
        [
            // ----- scenario/2 -----
            ("Scenario/2 round-trips a two-seat scenario and rejects v1 start", () =>
            {
                MultiScenarioDto scene = BaseScenario();
                string json = MultiScenarioCodec.Encode(scene);
                Check(json.Contains("\"format\":\"scenario/2\"") && json.Contains("\"rules\":\"facility-zero/2\""), "scenario/2 wire versions wrong.");
                Check(json.Contains("\"spawns\":[{\"x\":1,\"y\":1},{\"x\":3,\"y\":1}]"), "scenario/2 spawns wrong.");
                MultiScenarioDto round = MultiScenarioCodec.Parse(json);
                Equal(MultiScenarioCodec.Encode(round), json);
                Throws(() => MultiScenarioCodec.Parse(clone(scene, node => node["start"] = new JsonObject { ["x"] = 1, ["y"] = 1 })),
                    "scenario/2 must reject the v1 'start' field (unknown field).");
            }),
            ("Scenario/2 rejects wrong spawn count, off-floor, overlapping and out-of-bounds spawns", () =>
            {
                MultiScenarioDto s = BaseScenario();
                Throws(() => MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray())), "0 spawns accepted");
                Throws(() => MultiScenarioCodec.Parse(clone(s, node =>
                    node["spawns"] = new JsonArray(Point(1, 1), Point(1, 1), Point(1, 1), Point(1, 1), Point(1, 1)))), "5 spawns accepted");
                Throws(() => MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray(Point(1, 1), Point(2, 1)))), "spawn on key rejected");
                Throws(() => MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray(Point(1, 1), Point(6, 1)))), "spawn on core rejected");
                Throws(() => MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray(Point(1, 0), Point(3, 1)))), "spawn on wall rejected");
                Throws(() => MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray(Point(99, 1), Point(3, 1)))), "spawn out of bounds accepted");
                // Single spawn is legal (1..4); spawns may overlap each other and the exit.
                _ = MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray(Point(1, 1))));
                _ = MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray(Point(1, 1), Point(1, 1))));
                _ = MultiScenarioCodec.Parse(clone(s, node => node["spawns"] = new JsonArray(Point(1, 1), Point(7, 1))));
            }),
            // ----- agent/2 -----
            ("Agent/2 hello/observation/episode_end round-trip with the v1 observation body", () =>
            {
                AgentV2HelloMessage hello = new() { Limits = new AgentV2Limits { SeatCount = 2 } };
                string helloJson = AgentV2Codec.EncodeHello(hello);
                Check(helloJson == "{\"type\":\"hello\",\"protocol\":\"agent/2\",\"actions\":[\"move\",\"pickup\",\"interact\",\"wait\"],\"limits\":{\"seat_count\":2}}",
                    $"hello/2 wire wrong: {helloJson}");
                Equal("agent/2", ((AgentV2HelloMessage)AgentV2Codec.ParseHost(helloJson)).Protocol);
                Equal(2, ((AgentV2HelloMessage)AgentV2Codec.ParseHost(helloJson)).Limits.SeatCount);

                var obs = new AgentV2ObservationMessage
                {
                    RequestId = "r42", Tick = 7, Seat = 1,
                    Observation = new AgentObservationBody
                    {
                        Position = new PointDto { X = 1, Y = 1 },
                        Tiles = [new AgentTile { X = 1, Y = 1, Terrain = TerrainDto.Floor }],
                        Inventory = [ItemKindDto.Key],
                        Mission = MissionPhaseDto.OpenDoor,
                    },
                };
                string obsJson = AgentV2Codec.EncodeObservation(obs);
                var obsBack = (AgentV2ObservationMessage)AgentV2Codec.ParseHost(obsJson);
                Equal(1, obsBack.Seat);
                Equal(7L, obsBack.Tick);
                Equal(MissionPhaseDto.OpenDoor, obsBack.Observation.Mission);
                Equal("r42", obsBack.RequestId);

                var end = new AgentV2EpisodeEndMessage
                {
                    RequestId = "r9", Result = new EpisodeBody { Kind = EpisodeKindDto.Success },
                    Observation = new AgentObservationBody { Position = new PointDto { X = 7, Y = 1 }, Mission = MissionPhaseDto.Succeeded },
                };
                var endBack = (AgentV2EpisodeEndMessage)AgentV2Codec.ParseHost(AgentV2Codec.EncodeEpisodeEnd(end));
                Equal(EpisodeKindDto.Success, endBack.Result.Kind);
            }),
            ("Agent/2 ready/action parse with protocol agent/2 and reject agent/1", () =>
            {
                var ready = AgentV2Codec.ParseReady("{\"type\":\"ready\",\"protocol\":\"agent/2\",\"name\":\"seatbot\"}");
                Equal("agent/2", ready.Protocol);
                Equal("seatbot", ready.Name);
                var action = AgentV2Codec.ParseAction("{\"type\":\"action\",\"request_id\":\"r1\",\"action\":{\"type\":\"move\",\"direction\":\"east\"}}", "r1");
                Equal(ActionTypeDto.Move, action.Action.Type);
                Throws(() => AgentV2Codec.Parse("{\"type\":\"ready\",\"protocol\":\"agent/1\",\"name\":\"x\"}"), "agent/1 accepted");
                Throws(() => AgentV2Codec.ParseAction("{\"type\":\"action\",\"request_id\":\"r1\",\"action\":{\"type\":\"wait\"}}", "r2"), "wrong request_id");
            }),
            ("Agent/2 observation rejects seat out of range and unknown fields", () =>
            {
                string baseObs = AgentV2Codec.EncodeObservation(new AgentV2ObservationMessage
                { RequestId = "r", Tick = 0, Seat = 0, Observation = new AgentObservationBody { Position = new PointDto { X = 1, Y = 1 } } });
                Throws(() => AgentV2Codec.ParseHost(cloneJson(baseObs, node => node["seat"] = -1)), "seat -1 accepted");
                Throws(() => AgentV2Codec.ParseHost(cloneJson(baseObs, node => node["seat"] = 4)), "seat 4 accepted");
                Throws(() => AgentV2Codec.ParseHost(cloneJson(baseObs, node => node["extra"] = true)), "unknown field accepted");
                Throws(() => AgentV2Codec.ParseHost(cloneJson(baseObs, node => node.Remove("seat"))), "missing seat accepted");
                Throws(() => AgentV2Codec.ParseHost(baseObs.Replace("\"seat\":0", "\"seat\":0,\"seat\":1", StringComparison.Ordinal)), "duplicate seat accepted");
            }),
            // ----- observer/2 -----
            ("Observer/2 round-trips snapshot, step_batch and agent_status for spectator and seat views", () =>
            {
                foreach (string view in new[] { "spectator", "agent:0", "agent:1", "agent:2", "agent:3" })
                {
                    var status = new MultiAgentStatusMessage { RunId = "run", View = view, Seq = 1, AgentStatus = AgentStatusDto.Waiting };
                    Equal(MultiObserverCodec.Encode(status), MultiObserverCodec.Encode(MultiObserverCodec.Parse(MultiObserverCodec.Encode(status))));

                    var snap = new MultiSnapshotMessage
                    {
                        RunId = "run", View = view, BaseSeq = 0,
                        State = new ObserverState
                        {
                            VisibleRadius = 3,
                            Tiles = BothTiles(),
                            Entities = SeatEntities(view),
                        },
                    };
                    Equal(MultiObserverCodec.Encode(snap), MultiObserverCodec.Encode(MultiObserverCodec.Parse(MultiObserverCodec.Encode(snap))));

                    var batch = new MultiStepBatchMessage
                    {
                        RunId = "run", View = view, Seq = 1, Tick = 1, AgentStatus = AgentStatusDto.ActionReceived,
                        Events = [new ObserverEventDto { Type = ObserverEventTypeDto.Waited }],
                        Patch = new ObserverPatch
                        {
                            TileUpserts = [], EntityUpserts = SeatEntities(view),
                            VisibleTiles = [], Inventory = [], MissionPhase = MissionPhaseDto.FindKey,
                        },
                    };
                    var batchBack = (MultiStepBatchMessage)MultiObserverCodec.Parse(MultiObserverCodec.Encode(batch));
                    Equal(view, batchBack.View);
                }
            }),
            ("Observer/2 view string parsing and rejection", () =>
            {
                Equal(true, M2Protocol.ParseView("spectator").IsSpectator);
                for (int i = 0; i <= M2Protocol.MaxSeatIndex; i++)
                {
                    var v = M2Protocol.ParseView($"agent:{i}");
                    Check(!v.IsSpectator && v.Seat == i, $"agent:{i} misparsed");
                }
                foreach (string bad in new[] { "agent:4", "agent", "AGENT:0", "spectator:1", "agent:01", "", "agent:-1" })
                    Throws(() => M2Protocol.ParseView(bad), $"view '{bad}' accepted");
                // The observer envelope must go through the same gate. Build the JSON directly
                // (Encode itself validates and would reject before Parse is reached).
                foreach (string bad in new[] { "agent:4", "agent", "AGENT:0", "spectator:1" })
                {
                    string json = "{\"type\":\"agent_status\",\"protocol\":\"observer/2\",\"run_id\":\"r\"," +
                        $"\"view\":\"{bad}\",\"seq\":1,\"tick\":0,\"agent_status\":\"waiting\"}}";
                    Throws(() => MultiObserverCodec.Parse(json), $"observer/2 view '{bad}' accepted");
                }
            }),
            ("Observer/2 rejects a seat view referencing another seat's entity", () =>
            {
                // agent:0 referencing seat:1 must be rejected. Build raw JSON because Encode
                // itself rejects and would throw before Parse is reached.
                string otherSeat = "{\"type\":\"snapshot\",\"protocol\":\"observer/2\",\"run_id\":\"r\",\"view\":\"agent:0\",\"base_seq\":0,\"tick\":0,\"agent_status\":\"waiting\"," +
                    "\"state\":{\"visible_radius\":3,\"tiles\":[{\"x\":1,\"y\":1,\"terrain\":\"floor\",\"is_exit\":false,\"item\":null,\"door_open\":null,\"last_seen_tick\":null}]," +
                    "\"entities\":[{\"id\":\"seat:1\",\"kind\":\"agent\",\"x\":1,\"y\":1}],\"inventory\":[],\"mission_phase\":\"find_key\",\"episode\":null}}";
                Throws(() => MultiObserverCodec.Parse(otherSeat), "agent:0 referencing seat:1 accepted");
                string selfSeat = otherSeat.Replace("\"seat:1\"", "\"seat:0\"", StringComparison.Ordinal);
                _ = MultiObserverCodec.Parse(selfSeat);
                // Out-of-range seat id is rejected even in the spectator view.
                string outOfRange = otherSeat.Replace("\"seat:1\"", "\"seat:4\"", StringComparison.Ordinal).Replace("\"agent:0\"", "\"spectator\"", StringComparison.Ordinal);
                Throws(() => MultiObserverCodec.Parse(outOfRange), "seat:4 accepted");
                // A non-seat scene entity is allowed in any view.
                string scene = otherSeat.Replace("\"seat:1\"", "\"key_1\"", StringComparison.Ordinal).Replace("\"agent:0\"", "\"spectator\"", StringComparison.Ordinal);
                _ = MultiObserverCodec.Parse(scene);
            }),
            ("Observer/2 rejects missing required fields, unknown fields and unsafe seq", () =>
            {
                string status = MultiObserverCodec.Encode(new MultiAgentStatusMessage { RunId = "r", View = "spectator", Seq = 1 });
                foreach (string field in new[] { "type", "protocol", "run_id", "view", "seq", "tick", "agent_status" })
                    Throws(() => MultiObserverCodec.Parse(cloneJson(status, node => node.Remove(field))), $"missing {field}");
                Throws(() => MultiObserverCodec.Parse(cloneJson(status, node => node["extra"] = 1)), "unknown field");
                Throws(() => MultiObserverCodec.Parse(cloneJson(status, node => node["seq"] = -1)), "negative seq");
                Throws(() => MultiObserverCodec.Parse(cloneJson(status, node => node["seq"] = 9_007_199_254_740_992L)), "unsafe seq");
                Throws(() => MultiObserverCodec.Parse(cloneJson(status, node => node["protocol"] = "observer/1")), "wrong protocol");
                Throws(() => MultiObserverCodec.Parse(status.Replace("\"run_id\":\"r\"", "\"run_id\":\"r\",\"run_id\":\"r\"", StringComparison.Ordinal)), "duplicate run_id");
            }),
            // ----- replay/2 -----
            ("Replay/2 header, status, step and footer round-trip with a mandatory seat", () =>
            {
                MultiSnapshotMessage snap = new MultiSnapshotMessage
                {
                    RunId = "run", View = "spectator", BaseSeq = 0,
                    State = new ObserverState
                    {
                        VisibleRadius = 3,
                        Tiles = BothTiles(),
                        Entities = [new ObserverEntity { Id = "seat:0", Kind = "agent", X = 1, Y = 1 }, new ObserverEntity { Id = "seat:1", Kind = "agent", X = 3, Y = 1 }],
                    },
                };
                MultiReplayRunHeader header = new() { Scenario = BaseScenario(), InitialSnapshot = snap, AgentName = "seatbot" };
                string headerJson = MultiReplayCodec.Encode(header);
                Check(headerJson.Contains("\"replay\":\"replay/2\""), "replay/2 version wrong");
                MultiReplayRunHeader headerBack = (MultiReplayRunHeader)MultiReplayCodec.Parse(headerJson);
                Equal("facility-zero/2", headerBack.Rules);
                Equal(2, headerBack.Scenario.Spawns.Length);

                MultiStepBatchMessage batch = new MultiStepBatchMessage
                {
                    RunId = "run", View = "agent:0", Seq = 1, Tick = 1, AgentStatus = AgentStatusDto.ActionReceived,
                    Events = [new ObserverEventDto { Type = ObserverEventTypeDto.Waited }],
                    Patch = new ObserverPatch
                    {
                        EntityUpserts = [new ObserverEntity { Id = "seat:0", Kind = "agent", X = 1, Y = 1 }],
                        VisibleTiles = [], Inventory = [], MissionPhase = MissionPhaseDto.FindKey,
                    },
                };
                MultiReplayStepRecord step = new MultiReplayStepRecord
                {
                    Seq = 1, Tick = 1, Seat = 0, Action = new ActionRequestDto { Type = ActionTypeDto.Wait },
                    Outcome = new ActionFeedback { Status = ActionStatusDto.Applied }, CoreHash = new string('a', 64), ObserverBatch = batch,
                };
                string stepJson = MultiReplayCodec.Encode(step);
                Check(stepJson.Contains("\"seat\":0"), "step_record missing seat");
                MultiReplayStepRecord stepBack = (MultiReplayStepRecord)MultiReplayCodec.Parse(stepJson);
                Equal(0, stepBack.Seat);
                Equal(stepJson, MultiReplayCodec.Encode(stepBack));

                string statusJson = MultiReplayCodec.Encode(new MultiReplayStatusRecord
                { ObserverStatus = new MultiAgentStatusMessage { RunId = "run", View = "spectator", Seq = 1 } });
                _ = (MultiReplayStatusRecord)MultiReplayCodec.Parse(statusJson);

                string footerJson = MultiReplayCodec.Encode(new MultiReplayRunFooter { Status = "aborted", LastTick = 1 });
                var footerBack = (MultiReplayRunFooter)MultiReplayCodec.Parse(footerJson);
                Equal("aborted", footerBack.Status);
            }),
            ("Replay/2 step rejects missing/out-of-range seat and mismatched batch", () =>
            {
                MultiStepBatchMessage batch = new MultiStepBatchMessage
                {
                    RunId = "r", View = "agent:0", Seq = 1, Tick = 1,
                    Events = [new ObserverEventDto { Type = ObserverEventTypeDto.Waited }],
                    Patch = new ObserverPatch { VisibleTiles = [], Inventory = [], MissionPhase = MissionPhaseDto.FindKey },
                };
                MultiReplayStepRecord step = new MultiReplayStepRecord
                {
                    Seq = 1, Tick = 1, Seat = 0, Action = new ActionRequestDto { Type = ActionTypeDto.Wait },
                    Outcome = new ActionFeedback { Status = ActionStatusDto.Applied }, CoreHash = new string('a', 64), ObserverBatch = batch,
                };
                string stepJson = MultiReplayCodec.Encode(step);
                Throws(() => MultiReplayCodec.Parse(cloneJson(stepJson, node => node.Remove("seat"))), "missing seat accepted");
                Throws(() => MultiReplayCodec.Parse(cloneJson(stepJson, node => node["seat"] = -1)), "seat -1 accepted");
                Throws(() => MultiReplayCodec.Parse(cloneJson(stepJson, node => node["seat"] = 4)), "seat 4 accepted");
                Throws(() => MultiReplayCodec.Parse(cloneJson(stepJson, node => node["seq"] = 2)), "seq mismatch");
                Throws(() => MultiReplayCodec.Parse(cloneJson(stepJson, node => node["tick"] = 2)), "tick mismatch");
                Throws(() => MultiReplayCodec.Parse(cloneJson(stepJson, node => node["core_hash"] = "zz")), "bad hash");
            }),
            // ----- shared strictness -----
            ("v2 codecs reject oversized messages and invalid UTF-8", () =>
            {
                string big = new string(' ', MultiObserverCodec.MaxMessageBytes);
                Throws(() => MultiObserverCodec.Parse("{}" + big), "oversized observer accepted");
                Throws(() => MultiReplayCodec.Parse("{}" + big), "oversized replay accepted");
                Throws(() => MultiObserverCodec.Parse(new byte[] { (byte)'{', 0xff, (byte)'}' }), "invalid UTF-8");
                Throws(() => MultiObserverCodec.Parse(new byte[] { 0xef, 0xbb, 0xbf, (byte)'{', (byte)'}' }), "BOM accepted");
                Throws(() => MultiScenarioCodec.Parse(Encoding.UTF8.GetBytes("{}x")), "extra JSON content");
            }),
            ("v2 duplicate-field and unknown-envelope rejection", () =>
            {
                string snap = MultiObserverCodec.Encode(new MultiSnapshotMessage
                {
                    RunId = "r", View = "spectator", BaseSeq = 0,
                    State = new ObserverState { VisibleRadius = 3, Tiles = [], Entities = [], Inventory = [], MissionPhase = MissionPhaseDto.FindKey },
                });
                Throws(() => MultiObserverCodec.Parse(snap.Replace("\"base_seq\":0", "\"base_seq\":0,\"base_seq\":0", StringComparison.Ordinal)), "duplicate base_seq");
                Throws(() => MultiObserverCodec.Parse(cloneJson(snap, node => node["type"] = "bogus")), "unknown envelope type");
                Throws(() => MultiReplayCodec.Parse("{\"type\":\"bogus\"}"), "unknown replay type");
                Throws(() => MultiScenarioCodec.Parse(clone(BaseScenario(), node => node["extra"] = 1)), "unknown scenario field");
            }),
            ("v1 fixtures still parse through the unchanged v1 codecs (regression)", () =>
            {
                foreach (string line in File.ReadAllLines(Path.Combine(root, "tests", "Fixtures", "Protocol", "agent", "valid-agent-to-host.jsonl")))
                    _ = AgentResponseParser.Parse(line);
                foreach (string line in File.ReadAllLines(Path.Combine(root, "tests", "Fixtures", "Protocol", "observer", "valid-observer.jsonl")))
                    _ = ObserverCodec.Parse(line);
                foreach (string line in File.ReadAllLines(Path.Combine(root, "tests", "Fixtures", "Protocol", "replay", "valid-replay.jsonl")))
                    _ = ReplayCodec.Parse(line);
                _ = ScenarioCodec.Parse(File.ReadAllBytes(Path.Combine(root, "tests", "Fixtures", "Protocol", "scenario", "facility-small.json")));
                _ = ScenarioCodec.Parse(File.ReadAllBytes(Path.Combine(root, "tests", "Fixtures", "Protocol", "scenario", "facility-tall.json")));
            }),
        ];
        int failures = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
        }
        Console.WriteLine($"M7 Protocol v2: {checks.Length - failures}/{checks.Length} passed.");
        return failures;
    }
    // Helpers -------------------------------------------------------------------------------

    private static MultiScenarioDto BaseScenario() => new MultiScenarioDto
    {
        Spawns = [new PointDto { X = 1, Y = 1 }, new PointDto { X = 3, Y = 1 }],
        Exit = new PointDto { X = 7, Y = 1 }, Key = new PointDto { X = 2, Y = 1 },
        Door = new PointDto { X = 4, Y = 1 }, Core = new PointDto { X = 6, Y = 1 },
        Rows = ["#########", "#.......#", "#########"], MaxTicks = 512, VisibilityRadius = 3,
    };

    private static JsonObject Point(int x, int y) => new() { ["x"] = x, ["y"] = y };

    private static ObserverEntity[] SeatEntities(string view)
    {
        if (view == "spectator")
            return [new ObserverEntity { Id = "seat:0", Kind = "agent", X = 1, Y = 1 }, new ObserverEntity { Id = "seat:1", Kind = "agent", X = 3, Y = 1 }];
        int seat = int.Parse(view[6..]);
        return [new ObserverEntity { Id = $"seat:{seat}", Kind = "agent", X = 1, Y = 1 }];
    }

    private static ObserverTile[] BothTiles() => [new ObserverTile { X = 1, Y = 1, Terrain = TerrainDto.Floor }, new ObserverTile { X = 3, Y = 1, Terrain = TerrainDto.Floor }];

    private static string clone(MultiScenarioDto s, Action<JsonObject> edit)
    {
        JsonObject node = JsonNode.Parse(MultiScenarioCodec.Encode(s))!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static string cloneJson(string json, Action<JsonObject> edit)
    {
        JsonObject node = JsonNode.Parse(json)!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static void Throws(Action operation, string message)
    {
        try { operation(); throw new Exception(message); }
        catch (ProtocolException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}