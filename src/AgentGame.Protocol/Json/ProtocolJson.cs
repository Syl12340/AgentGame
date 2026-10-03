using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentGame.Protocol;

/// <summary>Frozen M0 wire limits and protocol / rule version strings.</summary>
public static class ProtocolLimits
{
    /// <summary>Maximum UTF-8 bytes allowed for a single JSONL message's content (project-plan §5.1).</summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>Maximum JSON nesting depth enforced per message.</summary>
    public const int MaxJsonDepth = 32;

    public const string AgentProtocol = "agent/1";
    public const string ObserverProtocol = "observer/1";
    public const string ReplayProtocol = "replay/1";
    public const string ScenarioProtocol = "scenario/1";
    public const string RulesVersion = "facility-zero/1";
    public const string CoreEncoding = "core-state/1";

    public const string DefaultGenerator = "manual/1";

    /// <summary>The single legal view identifier for M0.</summary>
    public const string ViewAgent = "agent";

    /// <summary>The four legal action names advertised in <c>hello</c>.</summary>
    public static string[] HelloActions => ["move", "pickup", "interact", "wait"];
}

/// <summary>Shared System.Text.Json options: snake_case field naming, deterministic, JSONL style.</summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            MaxDepth = ProtocolLimits.MaxJsonDepth,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Encode a DTO to a single JSONL line (no trailing newline).</summary>
    public static string EncodeLine<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Encode a DTO to UTF-8 bytes for a single JSONL line (no trailing newline).</summary>
    public static byte[] EncodeLineUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>Strict-read a JSON object, rejecting a non-object node at this position.</summary>
    public static JsonObjectFields ReadObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ProtocolException($"{context}: expected an object, got {element.ValueKind}.");
        return new JsonObjectFields(element, context);
    }
}