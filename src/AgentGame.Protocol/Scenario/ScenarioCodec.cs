using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentGame.Protocol;

/// <summary>
/// Strict scenario/1 reader/writer. This is an independent Protocol-side structural reader and
/// is NOT the Core scenario validator; it enforces the frozen scenario/1 wire contract only
/// (field set, ASCII map, point bounds, floor placement, object overlap, budgets).
/// </summary>
public static class ScenarioCodec
{
    public static string Encode(ScenarioDto scenario) => ProtocolJson.EncodeLine(scenario);

    public static ScenarioDto Parse(string json)
    {
        try { return Parse(new UTF8Encoding(false, true).GetBytes(json)); }
        catch (EncoderFallbackException e) { throw new ProtocolException("scenario: invalid Unicode.", e); }
    }

    public static ScenarioDto Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > ProtocolLimits.MaxLineBytes)
            throw new ProtocolException($"Scenario exceeds the {ProtocolLimits.MaxLineBytes}-byte limit.");

        using JsonDocument doc = ParseDocument(json);
        JsonObjectFields fields = ProtocolJson.ReadObject(doc.RootElement, "scenario");
        fields.RejectUnknown("format", "rules", "generator", "rows", "start", "exit", "key", "door", "core", "max_ticks", "visibility_radius", "seed", "generation_attempts", "reference_length");

        string format = fields.Require("format", JsonValueKind.String).GetString()!;
        if (format != ProtocolLimits.ScenarioProtocol)
            throw new ProtocolException($"scenario: unsupported format '{format}'.");
        string rules = fields.Require("rules", JsonValueKind.String).GetString()!;
        if (rules != ProtocolLimits.RulesVersion)
            throw new ProtocolException($"scenario: unsupported rules version '{rules}'.");
        string generator = fields.Require("generator", JsonValueKind.String).GetString()!;
        if (generator.Length == 0)
            throw new ProtocolException("scenario: generator must not be empty.");

        string[] rows = ReadRows(fields.Require("rows", JsonValueKind.Array));
        var scenario = new ScenarioDto
        {
            Format = format,
            Rules = rules,
            Generator = generator,
            Rows = rows,
            Seed = OptionalSeed(fields),
            GenerationAttempts = OptionalInt(fields, "generation_attempts"),
            ReferenceLength = OptionalInt(fields, "reference_length"),
            MaxTicks = ReadInt(fields.Require("max_ticks", JsonValueKind.Number), "max_ticks"),
            VisibilityRadius = ReadInt(fields.Require("visibility_radius", JsonValueKind.Number), "visibility_radius"),
            Start = ReadPoint(fields.Require("start", JsonValueKind.Object)),
            Exit = ReadPoint(fields.Require("exit", JsonValueKind.Object)),
            Key = ReadPoint(fields.Require("key", JsonValueKind.Object)),
            Door = ReadPoint(fields.Require("door", JsonValueKind.Object)),
            Core = ReadPoint(fields.Require("core", JsonValueKind.Object)),
        };

        if (scenario.MaxTicks < 1) throw new ProtocolException("scenario: max_ticks must be >= 1.");
        if (scenario.VisibilityRadius is < 0 or > 128) throw new ProtocolException("scenario: visibility_radius must be in [0, 128].");

        if (scenario.GenerationAttempts is < 1) throw new ProtocolException("scenario: generation_attempts must be positive.");
        if (scenario.ReferenceLength is < 1 || scenario.ReferenceLength > scenario.MaxTicks)
            throw new ProtocolException("scenario: reference_length must be within the action budget.");

        int width = rows[0].Length, height = rows.Length;
        foreach (PointDto p in new[] { scenario.Start, scenario.Exit, scenario.Key, scenario.Door, scenario.Core })
        {
            if (p.X < 0 || p.X >= width || p.Y < 0 || p.Y >= height)
                throw new ProtocolException("scenario: an object point is outside the map.");
            if (rows[p.Y][p.X] == '#')
                throw new ProtocolException("scenario: an object point is not on floor.");
        }
        PointDto[] unique = [scenario.Exit, scenario.Key, scenario.Door, scenario.Core];
        if (unique.Select(p => (p.X, p.Y)).Distinct().Count() != unique.Length)
            throw new ProtocolException("scenario: exit/key/door/core must not overlap.");
        if (Same(scenario.Start, scenario.Key) || Same(scenario.Start, scenario.Door) || Same(scenario.Start, scenario.Core))
            throw new ProtocolException("scenario: start overlaps a non-exit object.");

        return scenario;
    }

    private static string? OptionalSeed(JsonObjectFields fields)
    {
        if (!fields.TryGet("seed", out _)) return null;
        string seed = fields.Require("seed", JsonValueKind.String).GetString()!;
        if (!ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            (seed.Length > 1 && seed[0] == '0'))
            throw new ProtocolException("scenario: seed must be a canonical UInt64 decimal string.");
        return seed;
    }
    private static int? OptionalInt(JsonObjectFields fields, string name) =>
        fields.TryGet(name, out _) ? ReadInt(fields.Require(name, JsonValueKind.Number), name) : null;

    private static bool Same(PointDto a, PointDto b) => a.X == b.X && a.Y == b.Y;

    private static string[] ReadRows(JsonElement rows)
    {
        var list = new List<string>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.String)
                throw new ProtocolException("scenario.rows: each row must be a string.");
            list.Add(row.GetString()!);
        }
        if (list.Count < 1 || list.Count > 128) throw new ProtocolException("scenario.rows: height must be in [1, 128].");
        int width = list[0].Length;
        if (width < 1 || width > 128) throw new ProtocolException("scenario.rows: width must be in [1, 128].");
        foreach (string row in list)
        {
            if (row.Length != width)
                throw new ProtocolException("scenario.rows: rows must be rectangular.");
            foreach (char c in row)
                if (c is not ('.' or '#'))
                    throw new ProtocolException($"scenario.rows: illegal character '{c}' (only '.' and '#' are allowed).");
        }
        return list.ToArray();
    }

    private static int ReadInt(JsonElement el, string context)
    {
        if (!el.TryGetInt32(out int value)) throw new ProtocolException($"scenario: '{context}' is not a JSON integer.");
        return value;
    }

    private static PointDto ReadPoint(JsonElement el)
    {
        JsonObjectFields p = ProtocolJson.ReadObject(el, "scenario point");
        p.RejectUnknown("x", "y");
        return new PointDto { X = ReadInt(p.Require("x", JsonValueKind.Number), "x"), Y = ReadInt(p.Require("y", JsonValueKind.Number), "y") };
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> json)
    {
        try { _ = new UTF8Encoding(false, true).GetCharCount(json); }
        catch (DecoderFallbackException e) { throw new ProtocolException("scenario: invalid UTF-8.", e); }
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ProtocolLimits.MaxJsonDepth,
        });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new ProtocolException("scenario: expected a single JSON object.");
            JsonDocument doc = JsonDocument.ParseValue(ref reader);
            try
            {
                if (reader.Read()) throw new ProtocolException("scenario: extra JSON content after the object.");
                return doc;
            }
            catch { doc.Dispose(); throw; }
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"scenario: not a single valid JSON object: {e.Message}", e);
        }
    }
}