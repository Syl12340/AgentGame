using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>
/// Strict facade over one JSON object used by all parsers. It rejects duplicate fields on
/// read, and lets a caller reject unknown fields and require fields by exact snake_case name
/// with an expected <see cref="JsonValueKind"/> (and optionally forbid null).
/// </summary>
public sealed class JsonObjectFields
{
    private readonly Dictionary<string, JsonElement> _fields;
    private readonly string _context;

    internal JsonObjectFields(JsonElement element, string context)
    {
        _context = context;
        _fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (!_fields.TryAdd(prop.Name, prop.Value))
                throw new ProtocolException($"{context}: duplicate field '{prop.Name}'.");
        }
    }

    public IReadOnlyCollection<string> Names => _fields.Keys;

    /// <summary>Throw if any present field is not one of the exact snake_case names.</summary>
    public void RejectUnknown(params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (string name in _fields.Keys)
            if (!set.Contains(name))
                throw new ProtocolException($"{_context}: unknown field '{name}'.");
    }

    public bool TryGet(string name, out JsonElement value) => _fields.TryGetValue(name, out value);

    /// <summary>Require the field exists and has the given kind. A present null is always rejected.</summary>
    public JsonElement Require(string name, JsonValueKind kind)
    {
        if (!_fields.TryGetValue(name, out JsonElement value))
            throw new ProtocolException($"{_context}: missing required field '{name}'.");
        if (value.ValueKind == JsonValueKind.Null)
            throw new ProtocolException($"{_context}: field '{name}' must not be null.");
        if (value.ValueKind != kind)
            throw new ProtocolException($"{_context}: field '{name}' should be {kind}, got {value.ValueKind}.");
        return value;
    }

    /// <summary>A convenience guard: any provided field may hold a value of the given kind or null.</summary>
    public void Optional(string name, JsonValueKind kind)
    {
        if (!_fields.TryGetValue(name, out JsonElement value)) return;
        if (value.ValueKind == JsonValueKind.Null) throw new ProtocolException($"{_context}: field '{name}' must not be null.");
        if (value.ValueKind != kind) throw new ProtocolException($"{_context}: field '{name}' should be {kind}, got {value.ValueKind}.");
    }

    public JsonElement RequirePresent(string name)
    {
        if (!_fields.ContainsKey(name)) throw new ProtocolException($"{_context}: missing required field '{name}'.");
        return _fields[name];
    }
}