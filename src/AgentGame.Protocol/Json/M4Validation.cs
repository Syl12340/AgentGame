using System.Text;
using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>Shared structural guards for the observer/1 and replay/1 boundaries.</summary>
internal static class M4Validation
{
    internal const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Bytes(string json)
    {
        if (json is null) throw new ProtocolException("Message must not be null.");
        try { return StrictUtf8.GetBytes(json); }
        catch (EncoderFallbackException e) { throw new ProtocolException("Message contains invalid Unicode.", e); }
    }

    internal static JsonDocument Document(ReadOnlySpan<byte> json, int limit)
    {
        if (json.Length > 0 && json[^1] == (byte)'\n')
        {
            json = json[..^1];
            if (json.Length > 0 && json[^1] == (byte)'\r') json = json[..^1];
        }
        if (json.Length > limit) throw new ProtocolException($"Message exceeds the {limit}-byte limit.");
        if (json.IndexOfAny((byte)'\r', (byte)'\n') >= 0)
            throw new ProtocolException("Message must occupy one physical JSONL line.");
        if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            throw new ProtocolException("A UTF-8 BOM is not permitted.");
        try { _ = StrictUtf8.GetCharCount(json); }
        catch (DecoderFallbackException e) { throw new ProtocolException("Message contains invalid UTF-8.", e); }

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = ProtocolLimits.MaxJsonDepth,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("Message must be a single JSON object.");
            Inspect(document.RootElement);
            return document;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or EncoderFallbackException)
        {
            document?.Dispose();
            throw new ProtocolException($"Invalid JSON message: {e.Message}", e);
        }
        catch { document?.Dispose(); throw; }
    }

    // Include arbitrary footer stats: duplicate keys and invalid escaped Unicode are never valid.
    private static void Inspect(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                _ = StrictUtf8.GetByteCount(property.Name);
                if (!names.Add(property.Name)) throw new ProtocolException($"Duplicate field '{property.Name}'.");
                Inspect(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement value in element.EnumerateArray()) Inspect(value);
        else if (element.ValueKind == JsonValueKind.String)
            _ = StrictUtf8.GetByteCount(element.GetString()!);
    }

    internal static JsonObjectFields Object(JsonElement element, string context, params string[] allowed)
    {
        JsonObjectFields fields = ProtocolJson.ReadObject(element, context);
        fields.RejectUnknown(allowed);
        return fields;
    }

    internal static string String(JsonObjectFields fields, string name, bool nonempty = true)
    {
        string value = fields.Require(name, JsonValueKind.String).GetString()!;
        if (nonempty && string.IsNullOrWhiteSpace(value)) throw new ProtocolException($"'{name}' must not be empty.");
        return value;
    }

    internal static void Literal(JsonObjectFields fields, string name, string expected)
    {
        if (String(fields, name) != expected) throw new ProtocolException($"Unsupported '{name}'; expected '{expected}'.");
    }

    internal static void OptionalString(JsonObjectFields fields, string name)
    {
        if (fields.TryGet(name, out _)) _ = String(fields, name, false);
    }

    internal static long Safe(JsonObjectFields fields, string name) => Safe(fields.Require(name, JsonValueKind.Number), name);
    internal static long Safe(JsonElement value, string context)
    {
        if (!value.TryGetInt64(out long number) || number is < 0 or > MaxSafeInteger)
            throw new ProtocolException($"'{context}' must be a nonnegative JSON safe integer.");
        return number;
    }

    internal static int Int(JsonObjectFields fields, string name, int min, int max)
    {
        if (!fields.Require(name, JsonValueKind.Number).TryGetInt32(out int number) || number < min || number > max)
            throw new ProtocolException($"'{name}' must be an integer in [{min}, {max}].");
        return number;
    }

    internal static bool Bool(JsonObjectFields fields, string name)
    {
        JsonElement value = fields.RequirePresent(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ProtocolException($"'{name}' must be a boolean.");
        return value.GetBoolean();
    }

    internal static T Enum<T>(JsonObjectFields fields, string name) where T : struct, Enum =>
        EnumJson.Parse<T>(fields.Require(name, JsonValueKind.String), name);

    internal static (int X, int Y) Point(JsonElement value)
    {
        var fields = Object(value, "point", "x", "y");
        return (Int(fields, "x", 0, 127), Int(fields, "y", 0, 127));
    }

    internal static EpisodeBody? Episode(JsonObjectFields fields, string name)
    {
        JsonElement value = fields.RequirePresent(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        var episode = Object(value, "episode", "kind");
        return new EpisodeBody { Kind = Enum<EpisodeKindDto>(episode, "kind") };
    }

    internal static void Inventory(JsonElement value)
    {
        var seen = new HashSet<ItemKindDto>();
        foreach (JsonElement item in value.EnumerateArray())
            if (!seen.Add(EnumJson.Parse<ItemKindDto>(item, "inventory item")))
                throw new ProtocolException("Inventory contains duplicate items.");
    }

    internal static Dictionary<(int X, int Y), long?> Tiles(JsonElement array, long tick)
    {
        var result = new Dictionary<(int X, int Y), long?>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            var tile = Object(value, "tile", "x", "y", "terrain", "item", "is_exit", "door_open", "last_seen_tick");
            var position = (Int(tile, "x", 0, 127), Int(tile, "y", 0, 127));
            TerrainDto terrain = Enum<TerrainDto>(tile, "terrain");
            JsonElement item = tile.RequirePresent("item");
            if (item.ValueKind != JsonValueKind.Null) _ = EnumJson.Parse<ItemKindDto>(item, "item");
            bool isExit = Bool(tile, "is_exit");
            JsonElement door = tile.RequirePresent("door_open");
            if (door.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
                throw new ProtocolException("'door_open' must be null or a boolean.");
            JsonElement last = tile.RequirePresent("last_seen_tick");
            long? lastSeen = last.ValueKind == JsonValueKind.Null ? null : Safe(last, "last_seen_tick");
            if (lastSeen > tick) throw new ProtocolException("Tile history cannot be newer than the envelope tick.");
            if (terrain == TerrainDto.Wall && (item.ValueKind != JsonValueKind.Null || door.ValueKind != JsonValueKind.Null || isExit))
                throw new ProtocolException("A wall tile cannot contain an item, door or exit.");
            if (!result.TryAdd(position, lastSeen)) throw new ProtocolException("Duplicate tile coordinates.");
        }
        return result;
    }

    internal static Dictionary<string, (int X, int Y)> Entities(JsonElement array)
    {
        var result = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        foreach (JsonElement value in array.EnumerateArray())
        {
            var entity = Object(value, "entity", "id", "kind", "x", "y", "state");
            string id = String(entity, "id");
            _ = String(entity, "kind");
            OptionalString(entity, "state");
            var position = (Int(entity, "x", 0, 127), Int(entity, "y", 0, 127));
            if (!result.TryAdd(id, position)) throw new ProtocolException("Duplicate entity identifiers.");
        }
        return result;
    }

    internal static void State(JsonElement value, long tick)
    {
        var state = Object(value, "observer state", "visible_radius", "tiles", "entities", "inventory", "mission_phase", "episode");
        _ = Int(state, "visible_radius", 0, 128);
        var tiles = Tiles(state.Require("tiles", JsonValueKind.Array), tick);
        var entities = Entities(state.Require("entities", JsonValueKind.Array));
        foreach (var position in entities.Values)
            if (!tiles.ContainsKey(position))
                throw new ProtocolException("Snapshot entities must occupy known tiles.");
        Inventory(state.Require("inventory", JsonValueKind.Array));
        _ = Enum<MissionPhaseDto>(state, "mission_phase");
        _ = Episode(state, "episode");
    }

    internal static void Patch(JsonElement value, long tick)
    {
        var patch = Object(value, "observer patch", "tile_upserts", "entity_upserts", "entity_removals", "visible_tiles", "inventory", "mission_phase", "episode");
        var tiles = Tiles(patch.Require("tile_upserts", JsonValueKind.Array), tick);
        var entities = Entities(patch.Require("entity_upserts", JsonValueKind.Array));
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in patch.Require("entity_removals", JsonValueKind.Array).EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new ProtocolException("Entity removals must contain nonempty string identifiers.");
            string id = item.GetString()!;
            if (!removed.Add(id) || entities.ContainsKey(id)) throw new ProtocolException("Duplicate or conflicting entity removal.");
        }
        var visible = new HashSet<(int X, int Y)>();
        foreach (JsonElement point in patch.Require("visible_tiles", JsonValueKind.Array).EnumerateArray())
            if (!visible.Add(Point(point))) throw new ProtocolException("Duplicate visible coordinates.");
        foreach (var (position, lastSeen) in tiles)
            if (visible.Contains(position) != (lastSeen is null))
                throw new ProtocolException("Tile upsert visibility disagrees with the current visible set.");
        // Entity upserts can refer to remembered tiles in the previous state. Only the
        // stream reducer has enough state to check that the final tile is known.
        Inventory(patch.Require("inventory", JsonValueKind.Array));
        _ = Enum<MissionPhaseDto>(patch, "mission_phase");
        _ = Episode(patch, "episode");
    }

    internal static void Events(JsonElement array)
    {
        foreach (JsonElement value in array.EnumerateArray())
        {
            var fields = Object(value, "observer event", "type", "subject", "entity_id", "position", "reason", "phase");
            _ = Enum<ObserverEventTypeDto>(fields, "type");
            foreach (string name in new[] { "subject", "entity_id", "reason" }) OptionalString(fields, name);
            if (fields.TryGet("position", out JsonElement position))
            {
                // A blocked move can describe an attempted coordinate outside the map.
                var point = Object(position, "event position", "x", "y");
                _ = Int(point, "x", int.MinValue, int.MaxValue);
                _ = Int(point, "y", int.MinValue, int.MaxValue);
            }
            if (fields.TryGet("phase", out _)) _ = Enum<MissionPhaseDto>(fields, "phase");
        }
    }

    internal static void Hash(JsonObjectFields fields, string name)
    {
        string hash = String(fields, name);
        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ProtocolException($"'{name}' must be a lowercase SHA-256 hexadecimal string.");
    }

    internal static void Action(JsonElement value)
    {
        var action = Object(value, "action", "type", "direction");
        ActionTypeDto type = Enum<ActionTypeDto>(action, "type");
        bool directional = type is ActionTypeDto.Move or ActionTypeDto.Interact;
        bool present = action.TryGet("direction", out _);
        if (directional) _ = Enum<DirectionDto>(action, "direction");
        else if (present) throw new ProtocolException("Pickup and wait must not carry direction.");
    }

    internal static void Feedback(JsonElement value)
    {
        var feedback = Object(value, "action outcome", "status", "reason");
        _ = Enum<ActionStatusDto>(feedback, "status");
        OptionalString(feedback, "reason");
    }

    internal static T Deserialize<T>(JsonElement value)
    {
        try { return value.Deserialize<T>(ProtocolJson.Options) ?? throw new ProtocolException("Message deserialized to null."); }
        catch (JsonException e) { throw new ProtocolException($"Invalid message: {e.Message}", e); }
    }
}
