using System.Text;
using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>Structurally validate an agent/2 observation body (the frozen agent/1
/// AgentObservationBody shape) so host-encoded and decoded messages are strict.</summary>
internal static partial class AgentV2Body
{
    internal static void Observation(JsonElement value, string context)
    {
        var fields = M4Validation.Object(value, context, "position", "tiles", "inventory", "mission", "last_result", "episode");
        _ = M4Validation.Point(fields.Require("position", JsonValueKind.Object));

        var seen = new Dictionary<(int X, int Y), bool>();
        foreach (JsonElement tileEl in fields.Require("tiles", JsonValueKind.Array).EnumerateArray())
        {
            var tile = M4Validation.Object(tileEl, "observation tile", "x", "y", "terrain", "item", "is_exit", "door_open");
            var pos = (M4Validation.Int(tile, "x", 0, 127), M4Validation.Int(tile, "y", 0, 127));
            _ = M4Validation.Enum<TerrainDto>(tile, "terrain");
            // The item is nullable and may be absent or an explicit JSON null (mirrors agent/1).
            if (tile.TryGet("item", out JsonElement item) && item.ValueKind != JsonValueKind.Null)
                _ = EnumJson.Parse<ItemKindDto>(item, "tile item");
            _ = M4Validation.Bool(tile, "is_exit");
            // door_open is nullable; when present it must be null or a boolean.
            if (tile.TryGet("door_open", out JsonElement door) && door.ValueKind != JsonValueKind.Null)
            {
                if (door.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ProtocolException("'door_open' must be null or a boolean.");
            }
            if (!seen.TryAdd(pos, true)) throw new ProtocolException("Duplicate observation tile coordinates.");
        }
        M4Validation.Inventory(fields.Require("inventory", JsonValueKind.Array));
        _ = M4Validation.Enum<MissionPhaseDto>(fields, "mission");
        if (fields.TryGet("last_result", out JsonElement last) && last.ValueKind != JsonValueKind.Null)
            M4Validation.Feedback(last);
        if (fields.TryGet("episode", out JsonElement episode) && episode.ValueKind != JsonValueKind.Null)
            _ = M4Validation.Episode(fields, "episode");
    }
}

/// <summary>Strict agent/2 wire codec for the host→Agent messages (hello/observation/episode_end)
/// and the Agent→host replies (ready/action), mirroring v1's AgentResponseParser rigour.</summary>
public static class AgentV2Codec
{
    public const int MaxLineBytes = ProtocolLimits.MaxLineBytes;

    public static string EncodeHello(AgentV2HelloMessage message) => EncodeHost(message, m => ValidHello(m));
    public static string EncodeObservation(AgentV2ObservationMessage message) => EncodeHost(message, m => ValidObservation(m));
    public static string EncodeEpisodeEnd(AgentV2EpisodeEndMessage message) => EncodeHost(message, m => ValidEpisodeEnd(m));

    public static object ParseHost(string json) => ParseHost(EncodeInput(json));
    public static object ParseHost(ReadOnlySpan<byte> json) => ParseHostCore(json);

    // ----- Agent → host replies: ready / action (mirrors v1 AgentResponseParser) -----
    public static AgentResponse Parse(string line) => Parse(EncodeInput(line));
    public static ReadyResponse ParseReady(string line) => ParseReady(EncodeInput(line));
    public static ActionResponse ParseAction(string line, string expectedRequestId) => ParseAction(EncodeInput(line), expectedRequestId);

    public static AgentResponse Parse(ReadOnlySpan<byte> line)
    {
        AgentResponse response = ParseCore(line, phase: null);
        return response;
    }
    public static ReadyResponse ParseReady(ReadOnlySpan<byte> line) => (ReadyResponse)ParseCore(line, Phase.Ready);
    public static ActionResponse ParseAction(ReadOnlySpan<byte> line, string expectedRequestId)
    {
        AgentResponse response = ParseCore(line, Phase.Action);
        var action = (ActionResponse)response;
        if (!string.Equals(action.RequestId, expectedRequestId, StringComparison.Ordinal))
            throw new ProtocolException($"agent/2 response: request_id '{action.RequestId}' does not match the expected '{expectedRequestId}'.");
        return action;
    }

    private enum Phase { Ready, Action }

    private static AgentResponse ParseCore(ReadOnlySpan<byte> line, Phase? phase)
    {
        if (line.Length > 0 && line[^1] == (byte)'\n') line = line[..^1];
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (line.IndexOfAny((byte)'\n', (byte)'\r') >= 0)
            throw new ProtocolException("A JSONL message must occupy exactly one physical line.");
        if (line.Length > MaxLineBytes)
            throw new ProtocolException($"Agent response line exceeds the {MaxLineBytes}-byte limit.");
        _ = DecodeUtf8(line);

        using JsonDocument doc = ParseDocument(line);
        var fields = ProtocolJson.ReadObject(doc.RootElement, "agent/2 response");
        fields.RejectUnknown("type", "protocol", "name", "request_id", "action");
        string type = fields.Require("type", JsonValueKind.String).GetString()!;

        if (phase is Phase.Ready && type != "ready")
            throw new ProtocolException($"agent/2 response: handshake phase expects 'ready', got '{type}'.");
        if (phase is Phase.Action && type != "action")
            throw new ProtocolException($"agent/2 response: decision phase expects 'action', got '{type}'.");

        return type switch
        {
            "ready" => ParseReadyBody(fields),
            "action" => ParseActionBody(fields),
            _ => throw new ProtocolException($"agent/2 response: unexpected type '{type}'.")
        };
    }

    private static ReadyResponse ParseReadyBody(JsonObjectFields fields)
    {
        fields.RejectUnknown("type", "protocol", "name");
        string protocol = fields.Require("protocol", JsonValueKind.String).GetString()!;
        if (protocol != M2Protocol.AgentProtocol)
            throw new ProtocolException($"agent/2 response: unsupported protocol '{protocol}'.");
        string name = fields.Require("name", JsonValueKind.String).GetString()!;
        if (name.Length == 0)
            throw new ProtocolException("agent/2 response: ready has an empty name.");
        return new ReadyResponse { Protocol = protocol, Name = name };
    }

    private static ActionResponse ParseActionBody(JsonObjectFields fields)
    {
        fields.RejectUnknown("type", "request_id", "action");
        string requestId = fields.Require("request_id", JsonValueKind.String).GetString()!;
        if (requestId.Length == 0)
            throw new ProtocolException("agent/2 response: action has an empty request_id.");
        var action = ProtocolJson.ReadObject(fields.Require("action", JsonValueKind.Object), "agent/2 response.action");
        return new ActionResponse { RequestId = requestId, Action = ParseActionRequest(action) };
    }

    private static ActionRequestDto ParseActionRequest(JsonObjectFields action)
    {
        action.RejectUnknown("type", "direction");
        ActionTypeDto type = EnumJson.Parse<ActionTypeDto>(action.Require("type", JsonValueKind.String), "agent/2 response.action.type");
        bool directional = type is ActionTypeDto.Move or ActionTypeDto.Interact;
        if (!action.TryGet("direction", out JsonElement direction))
        {
            if (directional)
                throw new ProtocolException($"agent/2 response.action: 'direction' is required for '{type}'.");
            return new ActionRequestDto { Type = type, Direction = null };
        }
        if (directional)
        {
            DirectionDto dir = EnumJson.Parse<DirectionDto>(direction, "agent/2 response.action.direction");
            return new ActionRequestDto { Type = type, Direction = dir };
        }
        throw new ProtocolException($"agent/2 response.action: 'direction' must not be present on a '{type}' action.");
    }

    private static object ParseHostCore(ReadOnlySpan<byte> json)
    {
        using JsonDocument doc = M4Validation.Document(json, ProtocolLimits.MaxLineBytes);
        var fields = ProtocolJson.ReadObject(doc.RootElement, "agent/2 host message");
        string type = M4Validation.String(fields, "type");
        switch (type)
        {
            case "hello":
                fields.RejectUnknown("type", "protocol", "actions", "limits");
                M4Validation.Literal(fields, "protocol", M2Protocol.AgentProtocol);
                ReadActionList(fields.Require("actions", JsonValueKind.Array));
                var limits = M4Validation.Object(fields.Require("limits", JsonValueKind.Object), "hello.limits", "seat_count");
                _ = M4Validation.Int(limits, "seat_count", 1, 4);
                return M4Validation.Deserialize<AgentV2HelloMessage>(doc.RootElement);
            case "observation":
                return ReadObservation(doc.RootElement, fields);
            case "episode_end":
                fields.RejectUnknown("type", "request_id", "result", "observation");
                _ = M4Validation.String(fields, "request_id");
                M4Validation.Episode(fields, "result");
                AgentV2Body.Observation(fields.Require("observation", JsonValueKind.Object), "episode_end.observation");
                return M4Validation.Deserialize<AgentV2EpisodeEndMessage>(doc.RootElement);
            default: throw new ProtocolException($"Unsupported agent/2 host message type '{type}'.");
        }
    }

    private static object ReadObservation(JsonElement root, JsonObjectFields fields)
    {
        fields.RejectUnknown("type", "request_id", "tick", "seat", "observation");
        _ = M4Validation.String(fields, "request_id");
        _ = M4Validation.Safe(fields, "tick");
        _ = M4Validation.Int(fields, "seat", 0, M2Protocol.MaxSeatIndex);
        AgentV2Body.Observation(fields.Require("observation", JsonValueKind.Object), "observation.observation");
        return M4Validation.Deserialize<AgentV2ObservationMessage>(root);
    }

    private static void ReadActionList(JsonElement array)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new ProtocolException("hello.actions must contain nonempty string names.");
            string action = item.GetString()!;
            if (!seen.Add(action)) throw new ProtocolException("hello.actions contains duplicates.");
        }
    }

    private static void ValidHello(AgentV2HelloMessage message)
    {
        if (message is null) throw new ProtocolException("hello must not be null.");
        if (message.Protocol != M2Protocol.AgentProtocol) throw new ProtocolException("hello has an unsupported protocol.");
        if (message.Limits is null || message.Limits.SeatCount is < 1 or > 4)
            throw new ProtocolException("hello.limits.seat_count must be in [1, 4].");
        if (message.Actions is null) throw new ProtocolException("hello.actions must not be null.");
        _ = ParseHost(ProtocolJson.EncodeLine(message));
    }

    private static void ValidObservation(AgentV2ObservationMessage message)
    {
        if (message is null) throw new ProtocolException("observation must not be null.");
        if (message.Seat is < 0 or > M2Protocol.MaxSeatIndex) throw new ProtocolException("observation.seat must be in [0, 3].");
        _ = ParseHost(ProtocolJson.EncodeLine(message));
    }

    private static void ValidEpisodeEnd(AgentV2EpisodeEndMessage message)
    {
        if (message is null) throw new ProtocolException("episode_end must not be null.");
        _ = ParseHost(ProtocolJson.EncodeLine(message));
    }

    private static string EncodeHost<T>(T message, Action<T> validate)
    {
        validate(message);
        string json;
        try { json = JsonSerializer.Serialize(message, ProtocolJson.Options); }
        catch (JsonException e) { throw new ProtocolException($"agent/2 message cannot be encoded: {e.Message}", e); }
        return json;
    }

    private static byte[] EncodeInput(string line)
    {
        try { return new UTF8Encoding(false, true).GetBytes(line); }
        catch (EncoderFallbackException e) { throw new ProtocolException("Agent/2 response contains invalid Unicode.", e); }
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> line)
    {
        if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
            throw new ProtocolException("Agent/2 response starts with a UTF-8 byte-order mark.");
        try { return new UTF8Encoding(false, true).GetString(line); }
        catch (DecoderFallbackException e) { throw new ProtocolException("Agent/2 response is not valid UTF-8.", e); }
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line, new JsonReaderOptions
        {
            AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolLimits.MaxJsonDepth,
        });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new ProtocolException("Agent/2 response is not a single JSON object.");
            JsonDocument doc = JsonDocument.ParseValue(ref reader);
            try { if (reader.Read()) throw new ProtocolException("Agent/2 response contains extra JSON content after the object."); return doc; }
            catch { doc.Dispose(); throw; }
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"Agent/2 response is not one valid JSON object: {e.Message}", e);
        }
    }
}