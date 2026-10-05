using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>Strict observer/2 wire codec; it performs the same transport/DTD checks as the
/// observer/1 codec plus multi-seat view and seat/entity-id validation.</summary>
public static class MultiObserverCodec
{
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    public static object Parse(string json) => Parse(M4Validation.Bytes(json));

    public static object Parse(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = M4Validation.Document(json, MaxMessageBytes);
        return Read(document.RootElement);
    }

    public static string Encode(object message)
    {
        if (message is not (MultiSnapshotMessage or MultiStepBatchMessage or MultiAgentStatusMessage))
            throw new ProtocolException("Unsupported observer/2 message type.");
        string json;
        try { json = JsonSerializer.Serialize(message, message.GetType(), ProtocolJson.Options); }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { throw new ProtocolException($"observer/2 message cannot be encoded: {e.Message}", e); }
        _ = Parse(json);
        return json;
    }

    internal static object Read(JsonElement value)
    {
        var fields = ProtocolJson.ReadObject(value, "observer/2 envelope");
        string type = M4Validation.String(fields, "type");
        switch (type)
        {
            case "snapshot":
                fields.RejectUnknown("type", "protocol", "run_id", "view", "base_seq", "tick", "agent_status", "state");
                M2Protocol.M2View view = Envelope(fields);
                _ = M4Validation.Safe(fields, "base_seq");
                long tick = M4Validation.Safe(fields, "tick");
                JsonElement state = fields.Require("state", JsonValueKind.Object);
                M4Validation.State(state, tick);
                CheckStateEntities(state, view);
                return M4Validation.Deserialize<MultiSnapshotMessage>(value);
            case "step_batch":
                fields.RejectUnknown("type", "protocol", "run_id", "view", "seq", "tick", "agent_status", "events", "patch");
                M2Protocol.M2View batchView = Envelope(fields);
                _ = M4Validation.Safe(fields, "seq");
                long batchTick = M4Validation.Safe(fields, "tick");
                JsonElement events = fields.Require("events", JsonValueKind.Array);
                M4Validation.Events(events);
                CheckEvents(events, batchView);
                JsonElement patch = fields.Require("patch", JsonValueKind.Object);
                M4Validation.Patch(patch, batchTick);
                CheckPatch(patch, batchView);
                return M4Validation.Deserialize<MultiStepBatchMessage>(value);
            case "agent_status":
                fields.RejectUnknown("type", "protocol", "run_id", "view", "seq", "tick", "agent_status");
                _ = Envelope(fields);
                _ = M4Validation.Safe(fields, "seq");
                return M4Validation.Deserialize<MultiAgentStatusMessage>(value);
            default: throw new ProtocolException($"Unsupported observer/2 message type '{type}'.");
        }
    }

    /// <summary>Validate protocol/view/tick/agent_status; return the parsed view.</summary>
    private static M2Protocol.M2View Envelope(JsonObjectFields fields)
    {
        M4Validation.Literal(fields, "protocol", M2Protocol.ObserverProtocol);
        _ = M4Validation.String(fields, "run_id");
        M2Protocol.M2View view = M2Protocol.ParseView(M4Validation.String(fields, "view"));
        _ = M4Validation.Safe(fields, "tick");
        _ = M4Validation.Enum<AgentStatusDto>(fields, "agent_status");
        return view;
    }

    private static void CheckStateEntities(JsonElement state, M2Protocol.M2View view)
    {
        var fields = ProtocolJson.ReadObject(state, "observer/2 state");
        CheckEntities(fields.Require("entities", JsonValueKind.Array), view, "observer/2 state.entities");
    }

    private static void CheckPatch(JsonElement patch, M2Protocol.M2View view)
    {
        var fields = ProtocolJson.ReadObject(patch, "observer/2 patch");
        CheckEntities(fields.Require("entity_upserts", JsonValueKind.Array), view, "observer/2 patch.entity_upserts");
        if (fields.TryGet("entity_removals", out JsonElement removals))
            CheckRemovals(removals, view);
    }

    private static void CheckEntities(JsonElement array, M2Protocol.M2View view, string context)
    {
        foreach (JsonElement value in array.EnumerateArray())
        {
            var entity = ProtocolJson.ReadObject(value, "entity");
            if (!entity.TryGet("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String)
                throw new ProtocolException($"{context}: entity is missing a string id.");
            string id = idElement.GetString()!;
            CheckSeatId(id, view, context, entity.Require("kind", JsonValueKind.String).GetString() == "seat");
        }
    }

    private static void CheckRemovals(JsonElement array, M2Protocol.M2View view)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            string id = item.GetString()!;
            CheckSeatId(id, view, "observer/2 patch.entity_removals");
        }
    }

    private static void CheckEvents(JsonElement events, M2Protocol.M2View view)
    {
        foreach (JsonElement value in events.EnumerateArray())
        {
            var fields = ProtocolJson.ReadObject(value, "observer/2 event");
            foreach (string name in new[] { "entity_id", "subject" })
                if (fields.TryGet(name, out JsonElement id)) CheckSeatId(id.GetString()!, view, "observer/2 event." + name);
        }
    }

    private static void CheckSeatId(string id, M2Protocol.M2View view, string context, bool required = false)
    {
        if (!id.StartsWith("seat:", StringComparison.Ordinal) && !required) return;
        // Parse the canonical wire form directly. An overflowing numeric suffix must never
        // fall through as a generic scene entity; aliases such as seat:00 are not identities.
        if (id.Length != 6 || !id.StartsWith("seat:", StringComparison.Ordinal) || id[5] is < '0' or > '3')
            throw new ProtocolException($"{context}: '{id}' is not a canonical seat:0..3 identity.");
        int seat = id[5] - '0';
        if (!view.IsSpectator && view.Seat != seat)
            throw new ProtocolException($"{context}: '{view.Text}' cannot reference '{id}'.");
    }
}
