using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>Strict string→enum decoding: matches the exact snake_case wire spelling that the
/// encoder emits for a defined enum value, and rejects unknown/aliased values.</summary>
public static class EnumJson
{
    public static T Parse<T>(JsonElement element, string context) where T : struct, Enum
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new ProtocolException($"{context}: expected a string enum value, got {element.ValueKind}.");
        string text = element.GetString()!;
        foreach (T value in Enum.GetValues<T>())
            if (JsonSerializer.Serialize(value, ProtocolJson.Options).Trim('"') == text)
                return value;
        throw new ProtocolException($"{context}: unknown value '{text}'.");
    }
}