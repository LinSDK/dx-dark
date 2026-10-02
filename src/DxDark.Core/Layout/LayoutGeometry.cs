namespace DxDark.Core.Layout;

/// <summary>Where one LED's color comes from: an edge and a position along it.</summary>
/// <param name="Position">Centre along the edge, 0..1 (left→right or top→bottom).</param>
/// <param name="Span">Width of the LED's slot along the edge, as a fraction of the edge.</param>
/// <param name="Segment">Index of the segment the LED belongs to.</param>
public readonly record struct LedPlacement(ScreenEdge Edge, double Position, double Span, int Segment)
{
    public bool IsMapped => Edge != ScreenEdge.None;
}

/// <summary>Half-open pixel rectangle [X0, X1) × [Y0, Y1).</summary>
public readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;

    public int Height => Y1 - Y0;

    public int Area => Math.Max(0, Width) * Math.Max(0, Height);
}

public static class LayoutGeometry
{
    /// <summary>
    /// One placement per LED of the layout, in strip order. Each zone spreads its LEDs evenly
    /// along its whole edge, shifted by the zone's nudge.
    /// </summary>
    public static LedPlacement[] Placements(LedLayout layout)
    {
        var placements = new List<LedPlacement>(layout.LedCount);
        for (int s = 0; s < layout.Segments.Count; s++)
        {
            LedSegment segment = layout.Segments[s];
            int n = Math.Max(0, segment.LedCount);
            double step = n > 0 ? 1.0 / n : 0;
            for (int j = 0; j < n; j++)
            {
                // k counts in screen order (left→right / top→bottom), so the nudge moves in screen terms.
                int k = segment.Reversed ? n - 1 - j : j;
                placements.Add(new LedPlacement(segment.Edge, (k + 0.5 + segment.Nudge) * step, step, s));
            }
        }

        return placements.ToArray();
    }

    /// <summary>
    /// For each physical LED (0-based), the layout LED it shows, or -1 to keep it dark (LEDs
    /// beyond the zones).
    /// </summary>
    public static int[] PhysicalToLayout(int physicalCount, int layoutCount)
    {
        var map = new int[Math.Max(0, physicalCount)];
        for (int p = 0; p < map.Length; p++)
        {
            map[p] = p < layoutCount ? p : -1;
        }

        return map;
    }

    /// <summary>
    /// The pixel area an LED averages over.
    /// </summary>
    /// <param name="content">Picture area (the frame minus any black bars).</param>
    /// <param name="depth">How far into the picture to look, as a fraction of its height.</param>
    /// <param name="overlap">Extra width beyond the LED's own slot (0 = none, 1 = twice as wide).</param>
    public static PixelRect SampleRect(LedPlacement placement, PixelRect content, double depth, double overlap)
    {
        if (!placement.IsMapped || content.Width <= 0 || content.Height <= 0)
        {
            return default;
        }

        int band = (int)Math.Round(Math.Clamp(depth, 0.005, 0.5) * content.Height);
        band = Math.Max(1, band);
        double halfSpan = placement.Span * (1 + Math.Max(0, overlap)) / 2;
        double from = placement.Position - halfSpan;
        double to = placement.Position + halfSpan;

        int x0, x1, y0, y1;
        if (LedSegment.IsHorizontal(placement.Edge))
        {
            (x0, x1) = Along(from, to, content.X0, content.Width);
            (y0, y1) = placement.Edge == ScreenEdge.Top
                ? (content.Y0, Math.Min(content.Y1, content.Y0 + band))
                : (Math.Max(content.Y0, content.Y1 - band), content.Y1);
        }
        else
        {
            (y0, y1) = Along(from, to, content.Y0, content.Height);
            int bandX = Math.Min(band, content.Width);
            (x0, x1) = placement.Edge == ScreenEdge.Left
                ? (content.X0, content.X0 + bandX)
                : (content.X1 - bandX, content.X1);
        }

        return new PixelRect(x0, y0, x1, y1);
    }

    private static (int, int) Along(double from, double to, int origin, int length)
    {
        int a = origin + (int)Math.Floor(Math.Clamp(from, 0, 1) * length);
        int b = origin + (int)Math.Ceiling(Math.Clamp(to, 0, 1) * length);
        if (b <= a)
        {
            b = Math.Min(origin + length, a + 1);
            a = b - 1;
        }

        return (a, b);
    }
}
