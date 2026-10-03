using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>Strict observer/1 wire codec; it does not apply ordered-stream or game rules.</summary>
public static class ObserverCodec
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
        if (message is not (SnapshotMessage or StepBatchMessage or AgentStatusMessage))
            throw new ProtocolException("Unsupported Observer message type.");
        string json;
        try { json = JsonSerializer.Serialize(message, message.GetType(), ProtocolJson.Options); }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { throw new ProtocolException($"Observer message cannot be encoded: {e.Message}", e); }
        _ = Parse(json);
        return json;
    }

    internal static object Read(JsonElement value)
    {
        var fields = ProtocolJson.ReadObject(value, "observer envelope");
        string type = M4Validation.String(fields, "type");
        switch (type)
        {
            case "snapshot":
                fields.RejectUnknown("type", "protocol", "run_id", "view", "base_seq", "tick", "agent_status", "state");
                Envelope(fields);
                _ = M4Validation.Safe(fields, "base_seq");
                M4Validation.State(fields.Require("state", JsonValueKind.Object), M4Validation.Safe(fields, "tick"));
                return M4Validation.Deserialize<SnapshotMessage>(value);
            case "step_batch":
                fields.RejectUnknown("type", "protocol", "run_id", "view", "seq", "tick", "agent_status", "events", "patch");
                Envelope(fields);
                _ = M4Validation.Safe(fields, "seq");
                M4Validation.Events(fields.Require("events", JsonValueKind.Array));
                M4Validation.Patch(fields.Require("patch", JsonValueKind.Object), M4Validation.Safe(fields, "tick"));
                return M4Validation.Deserialize<StepBatchMessage>(value);
            case "agent_status":
                fields.RejectUnknown("type", "protocol", "run_id", "view", "seq", "tick", "agent_status");
                Envelope(fields);
                _ = M4Validation.Safe(fields, "seq");
                return M4Validation.Deserialize<AgentStatusMessage>(value);
            default: throw new ProtocolException($"Unsupported Observer message type '{type}'.");
        }
    }

    private static void Envelope(JsonObjectFields fields)
    {
        M4Validation.Literal(fields, "protocol", ProtocolLimits.ObserverProtocol);
        _ = M4Validation.String(fields, "run_id");
        M4Validation.Literal(fields, "view", ProtocolLimits.ViewAgent);
        _ = M4Validation.Safe(fields, "tick");
        _ = M4Validation.Enum<AgentStatusDto>(fields, "agent_status");
    }
}
