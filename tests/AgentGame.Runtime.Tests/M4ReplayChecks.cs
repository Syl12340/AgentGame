using System.Text;
using AgentGame.Core;
using AgentGame.Protocol;
using AgentGame.Runtime;
using AgentGame.Runtime.Observer;
using AgentGame.Runtime.Replay;

internal static class M4ReplayChecks
{
    public static int Run(string root)
    {
        string directory = Path.Combine(root, "artifacts", "m4-replay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ScenarioDto scene = ScenarioService.Read(Path.Combine(root, "tests", "Fixtures", "Core", "facility-small.json"));
        List<object> full = Records(scene);
        string FileOf(IEnumerable<object> records, string name = "replay")
        {
            string path = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(path, string.Join("\n", records.Select(ReplayCodec.Encode)) + "\n", new UTF8Encoding(false, true));
            return path;
        }
        string RawFile(string contents)
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(path, contents, new UTF8Encoding(false, true));
            return path;
        }
        string good = FileOf(full);
        var checks = new List<(string Name, Action Run)>
        {
            ("Completed replay verifies the independent final Core vector", () =>
            {
                var result = ReplayService.Verify(good);
                Require(result.Valid && result.Status == "completed", result.Error ?? "Verification failed.");
                Equal(13L, result.Tick);
                Equal("a68fe87640e7105142f481a59e77f43bdb854bab61a826e38ae4fd77ae323262", result.CoreHash);
            }),
            ("Export preserves the complete ordered Observer stream", () =>
            {
                var envelopes = new List<object>(); var summary = ReplayService.Export(good, envelopes.Add);
                Equal("completed", summary.Status); Equal(13L, summary.LastTick); Equal(26L, summary.LastSeq);
                Equal(27, envelopes.Count); Require(envelopes[0] is SnapshotMessage, "Initial snapshot was not exported.");
                Require(envelopes[1] is AgentStatusMessage { Seq: 1, Tick: 0 }, "Status wrapper lost identity.");
                Require(envelopes[^1] is StepBatchMessage { Seq: 26, Tick: 13 }, "Last batch was not preserved.");
            }),
            ("Aborted replay verifies the committed prefix without inventing a rule result", () =>
            {
                string path = FileOf(full.Take(3).Append(new ReplayRunFooter { Status = "aborted", Result = null, LastTick = 1 }));
                var result = ReplayService.Verify(path); Require(result.Valid && result.Status == "aborted", result.Error ?? "Bad prefix."); Equal(1L, result.Tick);
            }),
            ("Missing footer keeps readable prefix and reports incomplete", () =>
            {
                string path = FileOf(full.Take(full.Count - 1));
                var result = ReplayService.Verify(path); Require(!result.Valid, "Missing footer verified complete."); Equal("incomplete", result.Status);
                Equal(13L, result.Tick); Equal("incomplete", ReplayService.Export(path, _ => { }).Status);
            }),
            ("Unterminated final line is discarded as an uncommitted tail", () =>
            {
                string content = string.Join("\n", full.Take(3).Select(ReplayCodec.Encode)) + "\n{\"type\":\"run_footer\"";
                var result = ReplayService.Verify(RawFile(content)); Equal("incomplete", result.Status); Equal(1L, result.Tick);
            }),
            ("Complete malformed final line is corruption rather than a partial tail", () =>
            {
                string path = RawFile(ReplayCodec.Encode(full[0]) + "\n{broken}\n");
                var result = ReplayService.Verify(path); Equal("invalid_record", result.Error); Equal(2, result.Line);
            }),
            ("Corruption in the middle stops at its exact line", () =>
            {
                string path = RawFile(ReplayCodec.Encode(full[0]) + "\n{broken}\n" + ReplayCodec.Encode(full[2]) + "\n");
                var result = ReplayService.Verify(path); Equal("invalid_record", result.Error); Equal(2, result.Line); Equal(0L, result.Tick);
                using var reader = new ReplayReader(path);
                Equal(2, Throws<ReplayFormatException>(() => reader.ReadNext()).LineNumber);
                Equal(2, Throws<ReplayFormatException>(() => reader.ReadNext()).LineNumber);
            }),
            ("Status sequence gap is rejected before applying a later action", () =>
            {
                var status = (ReplayStatusRecord)full[1];
                string path = FileOf(new object[] { full[0], status with { ObserverStatus = status.ObserverStatus with { Seq = 2 } } });
                Equal("sequence_gap", ReplayService.Verify(path).Error);
            }),
            ("Status cannot advance the rule tick", () =>
            {
                var status = (ReplayStatusRecord)full[1];
                string path = FileOf(new object[] { full[0], status with { ObserverStatus = status.ObserverStatus with { Tick = 1 } } });
                Equal("status_tick_mismatch", ReplayService.Verify(path).Error);
            }),
            ("Observer run identity cannot change halfway through a replay", () =>
            {
                var status = (ReplayStatusRecord)full[1];
                string path = FileOf(new object[] { full[0], status with { ObserverStatus = status.ObserverStatus with { RunId = "other" } } });
                Equal("observer_identity_mismatch", ReplayService.Verify(path).Error);
            }),
            ("Step tick must advance exactly once", () =>
            {
                var step = (ReplayStepRecord)full[2];
                string path = FileOf(new object[] { full[0], full[1], step with { Tick = 2, ObserverBatch = step.ObserverBatch with { Tick = 2 } } });
                Equal("step_tick_mismatch", ReplayService.Verify(path).Error);
            }),
            ("Footer must describe the final committed tick", () =>
            {
                var footer = (ReplayRunFooter)full[^1];
                string path = FileOf(full.Take(full.Count - 1).Append(footer with { LastTick = 12 }));
                Equal("footer_tick_mismatch", ReplayService.Verify(path).Error);
            }),
            ("Completed footer cannot claim a different rule outcome", () =>
            {
                var footer = (ReplayRunFooter)full[^1];
                string path = FileOf(full.Take(full.Count - 1).Append(footer with { Result = new() { Kind = EpisodeKindDto.TurnLimit } }));
                Equal("footer_result_mismatch", ReplayService.Verify(path).Error);
            }),
            ("A second header is rejected", () =>
            { Equal("unexpected_record", ReplayService.Verify(FileOf(new[] { full[0], full[0] })).Error); }),
            ("Any data after footer is rejected including a blank line", () =>
            {
                var result = ReplayService.Verify(RawFile(File.ReadAllText(good) + "\n"));
                Equal("data_after_footer", result.Error); Equal(full.Count + 1, result.Line);
            }),
            ("Unterminated bytes after footer are rejected", () =>
            { Equal("data_after_footer", ReplayService.Verify(RawFile(File.ReadAllText(good) + "tail")).Error); }),
            ("Unsupported rules remain playable but cannot be verified", () =>
            {
                var header = (ReplayRunHeader)full[0];
                string path = FileOf(new object[] { header with { Rules = "future/2", Scenario = header.Scenario with { Rules = "future/2" } } }.Concat(full.Skip(1)));
                Equal("completed", ReplayService.Export(path, _ => { }).Status);
                Equal("unsupported_version", ReplayService.Verify(path).Status);
            }),
            ("Unsupported Core encoding is explicit", () =>
            {
                var header = (ReplayRunHeader)full[0];
                string path = FileOf(new object[] { header with { CoreEncoding = "core-state/2" } }.Concat(full.Skip(1)));
                Equal("unsupported_version", ReplayService.Verify(path).Error);
            }),
            ("Hash tampering is caught at the first affected step", () =>
            {
                var step = (ReplayStepRecord)full[2];
                string path = FileOf(full.Take(2).Append(step with { CoreHash = new string('0', 64) }).Concat(full.Skip(3)));
                var result = ReplayService.Verify(path); Equal("core_hash_mismatch", result.Error); Equal(3, result.Line); Equal(1L, result.Tick);
            }),
            ("Action feedback tampering is detected independently of the hash", () =>
            {
                var step = (ReplayStepRecord)full[2];
                string path = FileOf(full.Take(2).Append(step with { Outcome = new() { Status = ActionStatusDto.NoEffect, Reason = "tampered" } }).Concat(full.Skip(3)));
                Equal("outcome_mismatch", ReplayService.Verify(path).Error);
            }),
            ("Semantic event tampering is detected independently of the hash", () =>
            {
                var step = (ReplayStepRecord)full[2];
                string path = FileOf(full.Take(2).Append(step with { ObserverBatch = step.ObserverBatch with { Events = [new() { Type = ObserverEventTypeDto.Waited }] } }).Concat(full.Skip(3)));
                Equal("events_mismatch", ReplayService.Verify(path).Error);
            }),
            ("Observer patch tampering is detected independently of the hash", () =>
            {
                var step = (ReplayStepRecord)full[2];
                var changed = step with { ObserverBatch = step.ObserverBatch with
                    { Patch = step.ObserverBatch.Patch with { MissionPhase = MissionPhaseDto.OpenDoor } } };
                Equal("observer_patch_mismatch", ReplayService.Verify(FileOf(full.Take(2).Append(changed).Concat(full.Skip(3)))).Error);
            }),
            ("Initial snapshot must match the rules' true initial projection", () =>
            {
                var header = (ReplayRunHeader)full[0]; var tiles = header.InitialSnapshot.State.Tiles.ToArray();
                int floor = Array.FindIndex(tiles, tile => tile.Terrain == TerrainDto.Floor);
                Require(floor >= 0, "Fixture has no known floor tiles."); tiles[floor] = tiles[floor] with { IsExit = !tiles[floor].IsExit };
                var changed = header with { InitialSnapshot = header.InitialSnapshot with { State = header.InitialSnapshot.State with { Tiles = tiles } } };
                Equal("initial_snapshot_mismatch", ReplayService.Verify(FileOf(new object[] { changed }.Concat(full.Skip(1)))).Error);
            }),
            ("Reader header and exported callback arrays cannot mutate internal ownership", () =>
            {
                using var reader = new ReplayReader(good);
                ReplayRunHeader exposed = reader.Header; exposed.Scenario.Rows[1] = "#########"; exposed.InitialSnapshot.State.Tiles[0] = exposed.InitialSnapshot.State.Tiles[0] with { X = 999 };
                Require(reader.Header.Scenario.Rows[1] != "#########", "Header arrays shared."); Require(reader.Header.InitialSnapshot.State.Tiles[0].X != 999, "Snapshot arrays shared.");
                while (reader.ReadNext() is not null) { } Equal("completed", reader.Status);
                Equal("completed", ReplayService.Export(good, record =>
                { if (record is SnapshotMessage snapshot) Array.Clear(snapshot.State.Tiles); }).Status);
            }),
            ("Reader rejects oversized lines while retaining bounded buffers", () =>
            {
                string path = RawFile(ReplayCodec.Encode(full[0]) + "\n" + new string(' ', ReplayCodec.MaxRecordBytes + 1));
                var result = ReplayService.Verify(path); Equal("line_too_long", result.Error); Equal(2, result.Line);
                using var probe = new ReadProbe(Encoding.UTF8.GetBytes(File.ReadAllText(good)));
                using var reader = new ReplayReader(probe, true); while (reader.ReadNext() is not null) { }
                Require(probe.LargestRequestedRead <= 16 * 1024, "Reader tried to load the whole file.");
            }),
            ("Writer emits UTF8 without BOM and flushes every committed line", () =>
            {
                using var stream = new FlushProbe(); var writer = new ReplayFileWriter(stream, true);
                foreach (object record in full.Take(3)) writer.AppendAsync(record).AsTask().GetAwaiter().GetResult();
                writer.DisposeAsync().AsTask().GetAwaiter().GetResult(); Equal(3, stream.FlushCount);
                byte[] bytes = stream.ToArray(); Require(bytes[0] == (byte)'{' && bytes[^1] == (byte)'\n', "BOM or missing LF.");
                Equal(3, Encoding.UTF8.GetString(bytes).Count(c => c == '\n'));
            }),
            ("Write failure latches and prevents a later footer", () =>
            {
                using var stream = new WriteFailure(); var writer = new ReplayFileWriter(stream, true);
                Throws<IOException>(() => writer.AppendAsync(full[0]).AsTask().GetAwaiter().GetResult());
                Throws<InvalidOperationException>(() => writer.AppendAsync(full[^1]).AsTask().GetAwaiter().GetResult());
                Equal(1, stream.Attempts); writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }),
            ("Flush failure latches and prevents another record", () =>
            {
                using var stream = new FlushFailure(); var writer = new ReplayFileWriter(stream, true);
                Throws<IOException>(() => writer.AppendAsync(full[0]).AsTask().GetAwaiter().GetResult());
                long length = stream.Length;
                Throws<InvalidOperationException>(() => writer.AppendAsync(full[^1]).AsTask().GetAwaiter().GetResult());
                Equal(length, stream.Length); writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }),
            ("Replay writer does not overwrite an existing file", () =>
            {
                byte[] original = File.ReadAllBytes(good); Throws<IOException>(() => new ReplayFileWriter(good));
                Require(original.SequenceEqual(File.ReadAllBytes(good)), "Existing replay changed.");
            })
        };
        int failed = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"M4 Replay: {checks.Count - failed}/{checks.Count} passed.");
        return failed;
    }

    private static List<object> Records(ScenarioDto scene)
    {
        var game = Game.Create(ScenarioService.ToCoreScenario(scene));
        var projection = new ObserverProjection(game.Capture(), game.Observe());
        var header = new ReplayRunHeader
        {
            Scenario = scene, AgentName = "fixture", Generator = scene.Generator,
            InitialSnapshot = new() { RunId = "m4-test", BaseSeq = 0, Tick = 0, AgentStatus = AgentStatusDto.Waiting, State = projection.State }
        };
        var records = new List<object> { header }; long seq = 0;
        ActionRequestDto Move(DirectionDto d) => new() { Type = ActionTypeDto.Move, Direction = d };
        ActionRequestDto[] actions = [Move(DirectionDto.East), new() { Type = ActionTypeDto.Pickup },
            Move(DirectionDto.East), new() { Type = ActionTypeDto.Interact, Direction = DirectionDto.East },
            Move(DirectionDto.East), Move(DirectionDto.East), Move(DirectionDto.East), new() { Type = ActionTypeDto.Pickup },
            Move(DirectionDto.West), Move(DirectionDto.West), Move(DirectionDto.West), Move(DirectionDto.West), Move(DirectionDto.West)];
        foreach (var action in actions)
        {
            records.Add(new ReplayStatusRecord { ObserverStatus = new() { RunId = "m4-test", Seq = ++seq, Tick = game.Tick, AgentStatus = AgentStatusDto.Waiting } });
            StepResult result = game.Step(ReplayService.ToCore(action));
            records.Add(new ReplayStepRecord
            {
                Seq = ++seq, Tick = game.Tick, Action = action, CoreHash = StateEncoding.Hash(game.Capture()),
                Outcome = new() { Status = (ActionStatusDto)result.Outcome.Status, Reason = result.Outcome.Reason },
                ObserverBatch = new()
                {
                    RunId = "m4-test", Seq = seq, Tick = game.Tick, AgentStatus = AgentStatusDto.ActionReceived,
                    Events = ObserverProjection.Events(result), Patch = projection.Apply(game.Observe())
                }
            });
        }
        records.Add(new ReplayRunFooter { Status = "completed", Result = new() { Kind = EpisodeKindDto.Success }, LastTick = game.Tick });
        return records;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    private sealed class ReadProbe(byte[] bytes) : MemoryStream(bytes)
    {
        public int LargestRequestedRead { get; private set; }
        public override int Read(Span<byte> buffer) { LargestRequestedRead = Math.Max(LargestRequestedRead, buffer.Length); return base.Read(buffer); }
    }
    private class FlushProbe : MemoryStream
    {
        public int FlushCount { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken) { FlushCount++; return Task.CompletedTask; }
    }
    private sealed class WriteFailure : MemoryStream
    {
        public int Attempts { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Attempts++; return ValueTask.FromException(new IOException("Injected write failure.")); }
    }
    private sealed class FlushFailure : MemoryStream
    { public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new IOException("Injected flush failure.")); }
}
