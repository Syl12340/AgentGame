using System.Text;
using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>The strict-parse result of one Agent reply line.</summary>
public abstract record AgentResponse;

public sealed record ReadyResponse : AgentResponse
{
    public required string Protocol { get; init; }
    public required string Name { get; init; }
}

public sealed record ActionResponse : AgentResponse
{
    public required string RequestId { get; init; }
    public required ActionRequestDto Action { get; init; }
}

/// <summary>
/// Strict parser for the only two messages an Agent may send: <c>ready</c> and <c>action</c>.
///
/// The parser rejects: unknown / missing / extra / duplicate fields, a field of the wrong JSON
/// type, an unknown type/direction/request_id, any present null, prose or any content that is
/// not a single JSON object (Markdown fences around a line are therefore rejected as
/// not-JSON), an extra JSON value after the object, invalid UTF-8, a line whose content exceeds
/// 64 KiB, and JSON deeper than 32 levels.
///
/// Detection is by strict JSON shape only: a well-formed single JSON object is accepted even
/// when a string value contains characters such as a tab or backtick ("```"). Markdown/prose is
/// rejected only because it is not parseable as a single JSON object — never by scanning for
/// substrings or whitespace characters inside string values.
///
/// It never runs game rules; it only validates the transport DTD.
/// </summary>
public static class AgentResponseParser
{
    public static AgentResponse Parse(string line) => Parse(EncodeInput(line));

    /// <summary>Parse a string reply in the handshake "ready" phase (see the span overload).</summary>
    public static ReadyResponse ParseReady(string line) => ParseReady(EncodeInput(line));

    /// <summary>Parse a string reply in the "action" phase with a request_id match (see the span overload).</summary>
    public static ActionResponse ParseAction(string line, string expectedRequestId) => ParseAction(EncodeInput(line), expectedRequestId);

    /// <summary>
    /// Parse one Agent reply line into either a <see cref="ReadyResponse"/> or an
    /// <see cref="ActionResponse"/>, dispatching on <c>type</c>. The buffer should be a single
    /// line without the trailing line terminator, but a trailing '\n' or '\r' is tolerated.
    /// </summary>
    public static AgentResponse Parse(ReadOnlySpan<byte> line) => ParseCore(line, phase: null);

    /// <summary>Parse a line in the handshake "ready" phase. Only type <c>ready</c> is accepted;
    /// an <c>action</c> (or any other type) in this phase is rejected.</summary>
    public static ReadyResponse ParseReady(ReadOnlySpan<byte> line)
    {
        AgentResponse response = ParseCore(line, phase: Phase.Ready);
        return (ReadyResponse)response;
    }

    /// <summary>Parse a line in the "action" decision phase. Only type <c>action</c> is accepted
    /// (a <c>ready</c> here is rejected), and <c>request_id</c> must equal
    /// <paramref name="expectedRequestId"/>, otherwise the call is rejected.</summary>
    public static ActionResponse ParseAction(ReadOnlySpan<byte> line, string expectedRequestId)
    {
        AgentResponse response = ParseCore(line, phase: Phase.Action);
        var action = (ActionResponse)response;
        if (!string.Equals(action.RequestId, expectedRequestId, StringComparison.Ordinal))
            throw new ProtocolException($"agent response: request_id '{action.RequestId}' does not match the expected '{expectedRequestId}'.");
        return action;
    }

    private enum Phase { Ready, Action }

    private static AgentResponse ParseCore(ReadOnlySpan<byte> line, Phase? phase)
    {
        // Strip one line terminator if present so a plain "\n" JSONL line works.
        if (line.Length > 0 && line[^1] == (byte)'\n') line = line[..^1];
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (line.IndexOfAny((byte)'\n', (byte)'\r') >= 0)
            throw new ProtocolException("A JSONL message must occupy exactly one physical line.");

        if (line.Length > ProtocolLimits.MaxLineBytes)
            throw new ProtocolException($"Agent response line exceeds the {ProtocolLimits.MaxLineBytes}-byte limit.");

        _ = DecodeUtf8(line);

        using JsonDocument doc = ParseDocument(line);
        JsonObjectFields fields = ProtocolJson.ReadObject(doc.RootElement, "agent response");

        fields.RejectUnknown("type", "protocol", "name", "request_id", "action");
        string type = fields.Require("type", JsonValueKind.String).GetString()!;

        if (phase is Phase.Ready && type != "ready")
            throw new ProtocolException($"agent response: handshake phase expects 'ready', got '{type}'.");
        if (phase is Phase.Action && type != "action")
            throw new ProtocolException($"agent response: decision phase expects 'action', got '{type}'.");

        return type switch
        {
            "ready" => ParseReady(fields),
            "action" => ParseAction(fields),
            _ => throw new ProtocolException($"agent response: unexpected type '{type}'.")
        };
    }

    private static ReadyResponse ParseReady(JsonObjectFields fields)
    {
        fields.RejectUnknown("type", "protocol", "name");
        string protocol = fields.Require("protocol", JsonValueKind.String).GetString()!;
        if (protocol != ProtocolLimits.AgentProtocol)
            throw new ProtocolException($"agent response: unsupported protocol '{protocol}'.");
        string name = fields.Require("name", JsonValueKind.String).GetString()!;
        if (name.Length == 0)
            throw new ProtocolException("agent response: ready has an empty name.");
        return new ReadyResponse { Protocol = protocol, Name = name };
    }

    private static ActionResponse ParseAction(JsonObjectFields fields)
    {
        fields.RejectUnknown("type", "request_id", "action");
        string requestId = fields.Require("request_id", JsonValueKind.String).GetString()!;
        if (requestId.Length == 0)
            throw new ProtocolException("agent response: action has an empty request_id.");
        JsonObjectFields action = ProtocolJson.ReadObject(fields.Require("action", JsonValueKind.Object), "agent response.action");
        return new ActionResponse { RequestId = requestId, Action = ParseActionRequest(action) };
    }

    private static ActionRequestDto ParseActionRequest(JsonObjectFields action)
    {
        action.RejectUnknown("type", "direction");
        ActionTypeDto type = EnumJson.Parse<ActionTypeDto>(action.Require("type", JsonValueKind.String), "agent response.action.type");
        bool directional = type is ActionTypeDto.Move or ActionTypeDto.Interact;

        if (!action.TryGet("direction", out JsonElement direction))
        {
            if (directional)
                throw new ProtocolException($"agent response.action: 'direction' is required for '{type}'.");
            return new ActionRequestDto { Type = type, Direction = null };
        }
        if (directional)
        {
            DirectionDto dir = EnumJson.Parse<DirectionDto>(direction, "agent response.action.direction");
            return new ActionRequestDto { Type = type, Direction = dir };
        }
        throw new ProtocolException($"agent response.action: 'direction' must not be present on a '{type}' action.");
    }

    private static byte[] EncodeInput(string line)
    {
        try { return new UTF8Encoding(false, true).GetBytes(line); }
        catch (EncoderFallbackException e) { throw new ProtocolException("Agent response contains invalid Unicode.", e); }
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> line)
    {
        if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
            throw new ProtocolException("Agent response starts with a UTF-8 byte-order mark.");
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(line);
        }
        catch (DecoderFallbackException e)
        {
            throw new ProtocolException("Agent response is not valid UTF-8.", e);
        }
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolLimits.MaxJsonDepth,
        });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new ProtocolException("Agent response is not a single JSON object.");
            JsonDocument doc = JsonDocument.ParseValue(ref reader);
            try
            {
                if (reader.Read()) throw new ProtocolException("Agent response contains extra JSON content after the object.");
                return doc;
            }
            catch { doc.Dispose(); throw; }
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"Agent response is not one valid JSON object: {e.Message}", e);
        }
    }
}