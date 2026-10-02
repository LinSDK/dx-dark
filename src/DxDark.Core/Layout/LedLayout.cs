using System.Text.Json.Serialization;

namespace DxDark.Core.Layout;

/// <summary>Screen edge a zone of LEDs sits behind. <see cref="None"/> keeps LEDs dark.</summary>
public enum ScreenEdge
{
    Left,
    Top,
    Right,
    Bottom,
    None,
}

/// <summary>
/// One zone: a run of consecutive LEDs along one screen edge. Zones are listed in strip order,
/// starting at the end where the cable plugs in.
/// </summary>
public sealed class LedSegment
{
    /// <summary>Largest nudge, in LED widths, either way.</summary>
    public const double MaxNudge = 10;

    public ScreenEdge Edge { get; set; } = ScreenEdge.Top;

    /// <summary>LEDs in this zone.</summary>
    public int LedCount { get; set; } = 10;

    /// <summary>
    /// Orientation. False: LED numbers increase left→right (top/bottom edges) or top→bottom
    /// (left/right edges). True: the opposite direction.
    /// </summary>
    public bool Reversed { get; set; }

    /// <summary>
    /// Moves where this zone samples the screen, in LED widths: negative towards the left / top,
    /// positive towards the right / bottom. For strips mounted slightly off.
    /// </summary>
    public double Nudge { get; set; }

    public LedSegment Clone() => (LedSegment)MemberwiseClone();

    public static bool IsHorizontal(ScreenEdge edge) => edge is ScreenEdge.Top or ScreenEdge.Bottom;

    /// <summary>The orientation of a zone on <paramref name="edge"/> when the strip runs clockwise (as seen from the front).</summary>
    public static bool ClockwiseReversed(ScreenEdge edge) => edge is ScreenEdge.Left or ScreenEdge.Bottom;

    /// <summary>The next edge going clockwise around the screen.</summary>
    public static ScreenEdge NextClockwise(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => ScreenEdge.Top,
        ScreenEdge.Top => ScreenEdge.Right,
        ScreenEdge.Right => ScreenEdge.Bottom,
        _ => ScreenEdge.Left,
    };

    public static ScreenEdge NextCounterClockwise(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => ScreenEdge.Bottom,
        ScreenEdge.Bottom => ScreenEdge.Right,
        ScreenEdge.Right => ScreenEdge.Top,
        _ => ScreenEdge.Left,
    };

    /// <summary>
    /// A sensible next zone after <paramref name="previous"/>: the following edge in the direction
    /// the strip is running, with the matching orientation. The first zone starts up the left side,
    /// the way DX Light kits are wired.
    /// </summary>
    public static (ScreenEdge Edge, bool Reversed) Following(LedSegment? previous)
    {
        if (previous is null)
        {
            return (ScreenEdge.Left, ClockwiseReversed(ScreenEdge.Left));
        }

        bool clockwise = previous.Reversed == ClockwiseReversed(previous.Edge);
        ScreenEdge edge = clockwise ? NextClockwise(previous.Edge) : NextCounterClockwise(previous.Edge);
        return (edge, clockwise ? ClockwiseReversed(edge) : !ClockwiseReversed(edge));
    }
}

/// <summary>How the strip's LEDs are arranged around the screen, in strip order (LED 1 first).</summary>
public sealed class LedLayout
{
    public List<LedSegment> Segments { get; set; } = [];

    [JsonIgnore]
    public int LedCount => Segments.Sum(s => Math.Max(0, s.LedCount));

    /// <summary>True when zones cover all four edges, so the last LED sits next to the first.</summary>
    [JsonIgnore]
    public bool IsClosedLoop
    {
        get
        {
            var edges = Segments.Where(s => s.LedCount > 0).Select(s => s.Edge).ToHashSet();
            return edges.Contains(ScreenEdge.Left) && edges.Contains(ScreenEdge.Top)
                && edges.Contains(ScreenEdge.Right) && edges.Contains(ScreenEdge.Bottom);
        }
    }

    public LedLayout Clone() => new()
    {
        Segments = Segments.Select(s => s.Clone()).ToList(),
    };

    /// <summary>First LED index (0-based) of each zone.</summary>
    public int SegmentStart(int segmentIndex) =>
        Segments.Take(segmentIndex).Sum(s => Math.Max(0, s.LedCount));
}
