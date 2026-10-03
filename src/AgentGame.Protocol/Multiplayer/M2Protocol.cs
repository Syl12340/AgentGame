using System.Text.RegularExpressions;

namespace AgentGame.Protocol;

/// <summary>
/// Protocol / rule version strings and shared structural helpers for the frozen multi-seat
/// (Protocol "v2") wire contract. These intentionally live beside — but never mutate — the v1
/// <see cref="ProtocolLimits"/> strings so facility-zero/1 archives stay byte-compatible.
/// </summary>
public static partial class M2Protocol
{
    public const string AgentProtocol = "agent/2";
    public const string ObserverProtocol = "observer/2";
    public const string ReplayProtocol = "replay/2";
    public const string ScenarioProtocol = "scenario/2";
    public const string RulesVersion = "facility-zero/2";
    public const string CoreEncoding = "core-state/2";

    /// <summary>The spectator view identifier (full public map, never sent to a seat).</summary>
    public const string ViewSpectator = "spectator";

    /// <summary>Per-seat view prefix; the full view is <c>agent:&lt;0..3&gt;</c>.</summary>
    public const string ViewSeatPrefix = "agent:";

    /// <summary>Seat indices and per-seat entity ids live in [0, 3]; a seat is an int 0..3.</summary>
    public const int MaxSeatIndex = 3;

    /// <summary>The four legal action names advertised in <c>hello</c>.</summary>
    public static string[] HelloActions => ["move", "pickup", "interact", "wait"];

    /// <summary>A parsed view identifier: either the spectator view or a per-seat view.</summary>
    public readonly record struct M2View(bool IsSpectator, int? Seat)
    {
        public string Text => IsSpectator ? ViewSpectator : $"{ViewSeatPrefix}{Seat}";
    }

    /// <summary>
    /// Strictly parse a <c>view</c> string. Only the exact forms <c>spectator</c> and
    /// <c>agent:0</c>..<c>agent:3</c> are accepted (case-sensitive). Rejections include
    /// "agent", "AGENT:0", "spectator:1", "agent:4", empty and any other string.
    /// </summary>
    public static M2View ParseView(string view)
    {
        if (view == ViewSpectator) return new M2View(true, null);
        if (view.StartsWith(ViewSeatPrefix, StringComparison.Ordinal))
        {
            string suffix = view[ViewSeatPrefix.Length..];
            if (suffix.Length == 1 && char.IsAsciiDigit(suffix[0]) && int.Parse(suffix) <= MaxSeatIndex)
                return new M2View(false, int.Parse(suffix));
        }
        throw new ProtocolException(
            $"Unsupported view '{view}'; expected 'spectator' or 'agent:0'..'agent:{MaxSeatIndex}'.");
    }

    /// <summary>A seat entity id has the shape <c>seat:&lt;n&gt;</c>; the index range [0,3] is
    /// enforced by the callers (an out-of-range id is recognized as a seat id and rejected,
    /// never silently treated as a generic scene entity).</summary>
    public static bool IsSeatEntityId(string id) => SeatEntityIdPattern().IsMatch(id);

    public static int? SeatEntityIndex(string id)
    {
        Match match = SeatEntityIdPattern().Match(id);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int index)) return index;
        return null;
    }

    [GeneratedRegex(@"^seat:(\d+)$")]
    private static partial Regex SeatEntityIdPattern();
}