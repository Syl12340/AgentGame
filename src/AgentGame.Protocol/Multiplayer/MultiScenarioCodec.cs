using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>
/// Strict scenario/2 reader/writer. This is an independent Protocol-side structural reader and
/// is NOT the Core multi-seat validator; it enforces the frozen scenario/2 wire contract only
/// (field set, ASCII map, point bounds, floor placement, object overlap, spawn count, budgets).
/// </summary>
public static class MultiScenarioCodec
{
    /// <summary>The spawn array cardinality bounds for a scenario/2 layout.</summary>
    public const int MinSpawns = 1, MaxSpawns = 4;

    public static string Encode(MultiScenarioDto scenario) => ProtocolJson.EncodeLine(scenario);

    public static MultiScenarioDto Parse(string json)
    {
        try { return Parse(new UTF8Encoding(false, true).GetBytes(json)); }
        catch (EncoderFallbackException e) { throw new ProtocolException("scenario/2: invalid Unicode.", e); }
    }

    public static MultiScenarioDto Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > ProtocolLimits.MaxLineBytes)
            throw new ProtocolException($"Scenario exceeds the {ProtocolLimits.MaxLineBytes}-byte limit.");

        using JsonDocument doc = ParseDocument(json);
        JsonObjectFields fields = ProtocolJson.ReadObject(doc.RootElement, "scenario/2");
        fields.RejectUnknown("format", "rules", "generator", "rows", "spawns", "exit", "key", "door", "core", "max_ticks", "visibility_radius", "seed", "generation_attempts", "reference_length");

        string format = fields.Require("format", JsonValueKind.String).GetString()!;
        if (format != M2Protocol.ScenarioProtocol)
            throw new ProtocolException($"scenario/2: unsupported format '{format}'.");
        string rules = fields.Require("rules", JsonValueKind.String).GetString()!;
        if (rules != M2Protocol.RulesVersion)
            throw new ProtocolException($"scenario/2: unsupported rules version '{rules}'.");
        string generator = fields.Require("generator", JsonValueKind.String).GetString()!;
        if (generator.Length == 0)
            throw new ProtocolException("scenario/2: generator must not be empty.");

        string[] rows = ReadRows(fields.Require("rows", JsonValueKind.Array));
        PointDto[] spawns = ReadSpawns(fields.Require("spawns", JsonValueKind.Array));
        var scenario = new MultiScenarioDto
        {
            Format = format,
            Rules = rules,
            Generator = generator,
            Rows = rows,
            Spawns = spawns,
            Seed = OptionalSeed(fields),
            GenerationAttempts = OptionalInt(fields, "generation_attempts"),
            ReferenceLength = OptionalInt(fields, "reference_length"),
            MaxTicks = ReadInt(fields.Require("max_ticks", JsonValueKind.Number), "max_ticks"),
            VisibilityRadius = ReadInt(fields.Require("visibility_radius", JsonValueKind.Number), "visibility_radius"),
            Exit = ReadPoint(fields.Require("exit", JsonValueKind.Object)),
            Key = ReadPoint(fields.Require("key", JsonValueKind.Object)),
            Door = ReadPoint(fields.Require("door", JsonValueKind.Object)),
            Core = ReadPoint(fields.Require("core", JsonValueKind.Object)),
        };

        if (scenario.MaxTicks < 1) throw new ProtocolException("scenario/2: max_ticks must be >= 1.");
        if (scenario.VisibilityRadius is < 0 or > 128) throw new ProtocolException("scenario/2: visibility_radius must be in [0, 128].");

        if (scenario.GenerationAttempts is < 1) throw new ProtocolException("scenario/2: generation_attempts must be positive.");
        if (scenario.ReferenceLength is < 1 || scenario.ReferenceLength > scenario.MaxTicks)
            throw new ProtocolException("scenario/2: reference_length must be within the action budget.");

        int width = rows[0].Length, height = rows.Length;
        foreach (PointDto p in scenario.Spawns)
        {
            if (p.X < 0 || p.X >= width || p.Y < 0 || p.Y >= height)
                throw new ProtocolException("scenario/2: a spawn is outside the map.");
            if (rows[p.Y][p.X] == '#')
                throw new ProtocolException("scenario/2: a spawn is not on floor.");
        }
        foreach (PointDto p in new[] { scenario.Exit, scenario.Key, scenario.Door, scenario.Core })
        {
            if (p.X < 0 || p.X >= width || p.Y < 0 || p.Y >= height)
                throw new ProtocolException("scenario/2: an object point is outside the map.");
            if (rows[p.Y][p.X] == '#')
                throw new ProtocolException("scenario/2: an object point is not on floor.");
        }
        PointDto[] shared = [scenario.Exit, scenario.Key, scenario.Door, scenario.Core];
        if (shared.Select(p => (p.X, p.Y)).Distinct().Count() != shared.Length)
            throw new ProtocolException("scenario/2: exit/key/door/core must not overlap.");
        // A spawn must not overlap the shared key/door/core (the first spawn is the layout start,
        // so this also preserves scenario/1's "start does not overlap a non-exit object" rule).
        PointDto[] blockedPoints = [scenario.Key, scenario.Door, scenario.Core];
        HashSet<(int X, int Y)> occupied = blockedPoints
            .Select(p => (p.X, p.Y)).ToHashSet();
        foreach (PointDto sp in scenario.Spawns)
            if (occupied.Contains((sp.X, sp.Y)))
                throw new ProtocolException("scenario/2: a spawn overlaps the key, door or core.");

        return scenario;
    }

    private static string? OptionalSeed(JsonObjectFields fields)
    {
        if (!fields.TryGet("seed", out _)) return null;
        string seed = fields.Require("seed", JsonValueKind.String).GetString()!;
        if (!ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            (seed.Length > 1 && seed[0] == '0'))
            throw new ProtocolException("scenario/2: seed must be a canonical UInt64 decimal string.");
        return seed;
    }

    private static int? OptionalInt(JsonObjectFields fields, string name) =>
        fields.TryGet(name, out _) ? ReadInt(fields.Require(name, JsonValueKind.Number), name) : null;

    private static string[] ReadRows(JsonElement rows)
    {
        var list = new List<string>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.String)
                throw new ProtocolException("scenario/2.rows: each row must be a string.");
            list.Add(row.GetString()!);
        }
        if (list.Count < 1 || list.Count > 128) throw new ProtocolException("scenario/2.rows: height must be in [1, 128].");
        int width = list[0].Length;
        if (width < 1 || width > 128) throw new ProtocolException("scenario/2.rows: width must be in [1, 128].");
        foreach (string row in list)
        {
            if (row.Length != width)
                throw new ProtocolException("scenario/2.rows: rows must be rectangular.");
            foreach (char c in row)
                if (c is not ('.' or '#'))
                    throw new ProtocolException($"scenario/2.rows: illegal character '{c}' (only '.' and '#' are allowed).");
        }
        return list.ToArray();
    }

    private static PointDto[] ReadSpawns(JsonElement spawns)
    {
        var list = new List<PointDto>();
        foreach (JsonElement value in spawns.EnumerateArray())
            list.Add(ReadPoint(value));
        if (list.Count is < MinSpawns or > MaxSpawns)
            throw new ProtocolException($"scenario/2.spawns: count must be in [{MinSpawns}, {MaxSpawns}].");
        return list.ToArray();
    }

    private static int ReadInt(JsonElement el, string context)
    {
        if (!el.TryGetInt32(out int value)) throw new ProtocolException($"scenario/2: '{context}' is not a JSON integer.");
        return value;
    }

    private static PointDto ReadPoint(JsonElement el)
    {
        JsonObjectFields p = ProtocolJson.ReadObject(el, "scenario/2 point");
        p.RejectUnknown("x", "y");
        return new PointDto { X = ReadInt(p.Require("x", JsonValueKind.Number), "x"), Y = ReadInt(p.Require("y", JsonValueKind.Number), "y") };
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> json)
    {
        try { _ = new UTF8Encoding(false, true).GetCharCount(json); }
        catch (DecoderFallbackException e) { throw new ProtocolException("scenario/2: invalid UTF-8.", e); }
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolLimits.MaxJsonDepth,
        });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new ProtocolException("scenario/2: expected a single JSON object.");
            JsonDocument doc = JsonDocument.ParseValue(ref reader);
            try
            {
                if (reader.Read()) throw new ProtocolException("scenario/2: extra JSON content after the object.");
                return doc;
            }
            catch { doc.Dispose(); throw; }
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"scenario/2: not a single valid JSON object: {e.Message}", e);
        }
    }
}