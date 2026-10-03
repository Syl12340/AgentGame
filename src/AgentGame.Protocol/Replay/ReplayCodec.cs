using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentGame.Protocol;

/// <summary>Strict replay/1 record codec. Rule versions are metadata for playback, not execution authorization.</summary>
public static class ReplayCodec
{
    public const int MaxRecordBytes = 8 * 1024 * 1024;

    public static object Parse(string json) => Parse(M4Validation.Bytes(json));

    public static object Parse(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = M4Validation.Document(json, MaxRecordBytes);
        JsonElement value = document.RootElement;
        var fields = ProtocolJson.ReadObject(value, "replay record");
        string type = M4Validation.String(fields, "type");
        switch (type)
        {
            case "run_header":
                fields.RejectUnknown("type", "replay", "agent_protocol", "observer_protocol", "rules", "core_encoding", "generator", "agent_name", "scenario", "initial_snapshot");
                M4Validation.Literal(fields, "replay", ProtocolLimits.ReplayProtocol);
                M4Validation.Literal(fields, "observer_protocol", ProtocolLimits.ObserverProtocol);
                _ = M4Validation.String(fields, "agent_protocol");
                string rules = M4Validation.String(fields, "rules");
                _ = M4Validation.String(fields, "core_encoding");
                _ = M4Validation.String(fields, "generator");
                _ = M4Validation.String(fields, "agent_name", false);
                ScenarioDto scenario = PlaybackScenario(fields.Require("scenario", JsonValueKind.Object), rules);
                if (ObserverCodec.Read(fields.Require("initial_snapshot", JsonValueKind.Object)) is not SnapshotMessage snapshot)
                    throw new ProtocolException("Replay initial_snapshot must be a snapshot envelope.");
                if (snapshot.Tick != 0 || snapshot.BaseSeq != 0)
                    throw new ProtocolException("Replay initial_snapshot tick and base_seq must both be zero.");
                foreach (ObserverTile tile in snapshot.State.Tiles)
                    if (tile.X >= scenario.Rows[0].Length || tile.Y >= scenario.Rows.Length)
                        throw new ProtocolException("Initial snapshot tile lies outside its scenario.");
                return M4Validation.Deserialize<ReplayRunHeader>(value) with { Scenario = scenario };
            case "status_record":
                fields.RejectUnknown("type", "observer_status");
                if (ObserverCodec.Read(fields.Require("observer_status", JsonValueKind.Object)) is not AgentStatusMessage)
                    throw new ProtocolException("observer_status must be an agent_status envelope.");
                return M4Validation.Deserialize<ReplayStatusRecord>(value);
            case "step_record":
                fields.RejectUnknown("type", "seq", "tick", "action", "outcome", "core_hash", "view_hash", "observer_batch");
                long seq = M4Validation.Safe(fields, "seq"), tick = M4Validation.Safe(fields, "tick");
                M4Validation.Action(fields.Require("action", JsonValueKind.Object));
                M4Validation.Feedback(fields.Require("outcome", JsonValueKind.Object));
                M4Validation.Hash(fields, "core_hash");
                if (fields.TryGet("view_hash", out _)) M4Validation.Hash(fields, "view_hash");
                if (ObserverCodec.Read(fields.Require("observer_batch", JsonValueKind.Object)) is not StepBatchMessage batch)
                    throw new ProtocolException("observer_batch must be a step_batch envelope.");
                if (seq != batch.Seq || tick != batch.Tick)
                    throw new ProtocolException("Replay step seq/tick must match its Observer batch.");
                return M4Validation.Deserialize<ReplayStepRecord>(value);
            case "run_footer":
                fields.RejectUnknown("type", "status", "result", "last_tick", "stats");
                string status = M4Validation.String(fields, "status");
                if (status is not ("completed" or "aborted")) throw new ProtocolException("Replay footer status must be completed or aborted.");
                _ = M4Validation.Episode(fields, "result");
                _ = M4Validation.Safe(fields, "last_tick");
                return M4Validation.Deserialize<ReplayRunFooter>(value);
            default: throw new ProtocolException($"Unsupported Replay record type '{type}'.");
        }
    }

    public static string Encode(object record)
    {
        if (record is not (ReplayRunHeader or ReplayStatusRecord or ReplayStepRecord or ReplayRunFooter))
            throw new ProtocolException("Unsupported Replay record type.");
        string json;
        try { json = JsonSerializer.Serialize(record, record.GetType(), ProtocolJson.Options); }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { throw new ProtocolException($"Replay record cannot be encoded: {e.Message}", e); }
        _ = Parse(json);
        return json;
    }

    private static ScenarioDto PlaybackScenario(JsonElement value, string rules)
    {
        var fields = ProtocolJson.ReadObject(value, "replay scenario");
        if (M4Validation.String(fields, "rules") != rules)
            throw new ProtocolException("Replay scenario rules must equal header rules.");
        // The existing structural codec intentionally only accepts executable current rules.
        // Playback preserves the original metadata, while still applying every scenario/1 shape guard.
        JsonObject normalized = JsonNode.Parse(value.GetRawText())!.AsObject();
        normalized["rules"] = ProtocolLimits.RulesVersion;
        return ScenarioCodec.Parse(normalized.ToJsonString(ProtocolJson.Options)) with { Rules = rules };
    }
}
