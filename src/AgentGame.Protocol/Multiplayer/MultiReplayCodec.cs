using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentGame.Protocol;

/// <summary>Strict replay/2 record codec. Rule versions are metadata for playback, not execution
/// authorization; every step_record carries a mandatory seat.</summary>
public static class MultiReplayCodec
{
    public const int MaxRecordBytes = 8 * 1024 * 1024;

    public static object Parse(string json) => Parse(M4Validation.Bytes(json));

    public static object Parse(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = M4Validation.Document(json, MaxRecordBytes);
        JsonElement value = document.RootElement;
        var fields = ProtocolJson.ReadObject(value, "replay/2 record");
        string type = M4Validation.String(fields, "type");
        switch (type)
        {
            case "run_header":
                fields.RejectUnknown("type", "replay", "agent_protocol", "observer_protocol", "rules", "core_encoding", "generator", "agent_name", "scenario", "initial_snapshot");
                M4Validation.Literal(fields, "replay", M2Protocol.ReplayProtocol);
                M4Validation.Literal(fields, "observer_protocol", M2Protocol.ObserverProtocol);
                _ = M4Validation.String(fields, "agent_protocol");
                string rules = M4Validation.String(fields, "rules");
                _ = M4Validation.String(fields, "core_encoding");
                _ = M4Validation.String(fields, "generator");
                _ = M4Validation.String(fields, "agent_name", false);
                MultiScenarioDto scenario = PlaybackScenario(fields.Require("scenario", JsonValueKind.Object), rules);
                if (MultiObserverCodec.Read(fields.Require("initial_snapshot", JsonValueKind.Object)) is not MultiSnapshotMessage snapshot)
                    throw new ProtocolException("Replay initial_snapshot must be an observer/2 snapshot envelope.");
                if (snapshot.Tick != 0 || snapshot.BaseSeq != 0)
                    throw new ProtocolException("Replay initial_snapshot tick and base_seq must both be zero.");
                foreach (ObserverTile tile in snapshot.State.Tiles)
                    if (tile.X >= scenario.Rows[0].Length || tile.Y >= scenario.Rows.Length)
                        throw new ProtocolException("Initial snapshot tile lies outside its scenario.");
                return M4Validation.Deserialize<MultiReplayRunHeader>(value) with { Scenario = scenario };

            case "status_record":
                fields.RejectUnknown("type", "observer_status");
                if (MultiObserverCodec.Read(fields.Require("observer_status", JsonValueKind.Object)) is not MultiAgentStatusMessage)
                    throw new ProtocolException("observer_status must be an observer/2 agent_status envelope.");
                return M4Validation.Deserialize<MultiReplayStatusRecord>(value);

            case "step_record":
                fields.RejectUnknown("type", "seq", "tick", "seat", "action", "outcome", "core_hash", "observer_batch");
                long seq = M4Validation.Safe(fields, "seq");
                long tick = M4Validation.Safe(fields, "tick");
                _ = M4Validation.Int(fields, "seat", 0, M2Protocol.MaxSeatIndex);
                M4Validation.Action(fields.Require("action", JsonValueKind.Object));
                M4Validation.Feedback(fields.Require("outcome", JsonValueKind.Object));
                M4Validation.Hash(fields, "core_hash");
                if (MultiObserverCodec.Read(fields.Require("observer_batch", JsonValueKind.Object)) is not MultiStepBatchMessage batch)
                    throw new ProtocolException("observer_batch must be an observer/2 step_batch envelope.");
                if (seq != batch.Seq || tick != batch.Tick)
                    throw new ProtocolException("Replay step_seq/tick must match its observer/2 batch.");
                return M4Validation.Deserialize<MultiReplayStepRecord>(value);

            case "run_footer":
                fields.RejectUnknown("type", "status", "result", "last_tick", "stats");
                string status = M4Validation.String(fields, "status");
                if (status is not ("completed" or "aborted")) throw new ProtocolException("Replay footer status must be completed or aborted.");
                _ = M4Validation.Episode(fields, "result");
                _ = M4Validation.Safe(fields, "last_tick");
                return M4Validation.Deserialize<MultiReplayRunFooter>(value);

            default: throw new ProtocolException($"Unsupported replay/2 record type '{type}'.");
        }
    }

    public static string Encode(object record)
    {
        if (record is not (MultiReplayRunHeader or MultiReplayStatusRecord or MultiReplayStepRecord or MultiReplayRunFooter))
            throw new ProtocolException("Unsupported replay/2 record type.");
        string json;
        try { json = JsonSerializer.Serialize(record, record.GetType(), ProtocolJson.Options); }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { throw new ProtocolException($"Replay/2 record cannot be encoded: {e.Message}", e); }
        _ = Parse(json);
        return json;
    }

    private static MultiScenarioDto PlaybackScenario(JsonElement value, string rules)
    {
        var fields = ProtocolJson.ReadObject(value, "replay/2 scenario");
        if (M4Validation.String(fields, "rules") != rules)
            throw new ProtocolException("Replay/2 scenario rules must equal header rules.");
        // The structural codec intentionally only accepts executable current rules; playback
        // preserves the original metadata while applying every scenario/2 shape guard.
        JsonObject normalized = JsonNode.Parse(value.GetRawText())!.AsObject();
        normalized["rules"] = M2Protocol.RulesVersion;
        return MultiScenarioCodec.Parse(normalized.ToJsonString(ProtocolJson.Options)) with { Rules = rules };
    }
}