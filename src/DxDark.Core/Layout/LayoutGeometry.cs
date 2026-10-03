namespace DxDark.Core.Layout;

/// <summary>Where one LED sits: an edge and a position along it.</summary>
/// <param name="Position">Centre along the edge, 0..1 (left→right or top→bottom).</param>
/// <param name="Span">Width of the LED's slot along the edge, as a fraction of the edge.</param>
/// <param name="Segment">Index of the segment (zone) the LED belongs to.</param>
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

/// <summary>
/// The part of the picture a zone samples, as fractions of the picture (black bars excluded):
/// 0,0 is the top-left corner, 1,1 the bottom-right. The zone's LEDs share it out along the edge
/// direction. It may reach a little outside the picture; sampling stops at the picture's edge.
/// </summary>
/// <param name="Overlap">Extra width of each LED's slice beyond its own share (0 = none, 1 = double).</param>
public readonly record struct ZoneArea(double X, double Y, double Width, double Height, double Overlap)
{
    public const double MinSize = 0.02;

    /// <summary>The same area brought into the allowed range (at least 2 % in size, mostly on the picture).</summary>
    public ZoneArea Clamped()
    {
        double w = Math.Clamp(Width, MinSize, 1.5), h = Math.Clamp(Height, MinSize, 1.5);
        return new ZoneArea(
            Math.Clamp(X, -w + MinSize, 1 - MinSize),
            Math.Clamp(Y, -h + MinSize, 1 - MinSize),
            w,
            h,
            Math.Clamp(Overlap, 0, 3));
    }

    /// <summary>
    /// The usual area for a zone on <paramref name="edge"/>: the whole edge, <paramref name="depth"/>
    /// of the picture height deep (the same number of pixels on every edge).
    /// </summary>
    /// <param name="aspect">Width / height of the picture.</param>
    public static ZoneArea Default(ScreenEdge edge, double depth, double overlap, double aspect)
    {
        double d = Math.Clamp(depth, 0.005, 0.5);
        double side = Math.Min(1, d / Math.Max(0.1, aspect)); // the same pixels, as a share of the width
        return edge switch
        {
            ScreenEdge.Top => new ZoneArea(0, 0, 1, d, overlap),
            ScreenEdge.Bottom => new ZoneArea(0, 1 - d, 1, d, overlap),
            ScreenEdge.Left => new ZoneArea(0, 0, side, 1, overlap),
            ScreenEdge.Right => new ZoneArea(1 - side, 0, side, 1, overlap),
            _ => new ZoneArea(0, 0, 1, d, overlap),
        };
    }
}

public static class LayoutGeometry
{
    /// <summary>
    /// One placement per LED of the layout, in strip order: each zone spreads its LEDs evenly
    /// along its whole edge (where they physically are).
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
                // k counts in screen order (left→right / top→bottom).
                int k = segment.Reversed ? n - 1 - j : j;
                placements.Add(new LedPlacement(segment.Edge, (k + 0.5) * step, step, s));
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

    /// <summary>The area each zone samples with <paramref name="profile"/>: its own, else the usual one for its edge.</summary>
    /// <param name="aspect">Width / height of the picture.</param>
    public static ZoneArea[] Areas(LedLayout layout, Profiles.Profile profile, double aspect)
    {
        var areas = new ZoneArea[layout.Segments.Count];
        for (int s = 0; s < areas.Length; s++)
        {
            areas[s] = profile.AreaAt(s)?.Clamped()
                ?? ZoneArea.Default(layout.Segments[s].Edge, profile.SampleDepth, profile.ZoneOverlap, aspect);
        }

        return areas;
    }

    /// <summary>The pixel area one LED averages over: its share of its zone's area.</summary>
    /// <param name="content">Picture area (the frame minus any black bars).</param>
    public static PixelRect SampleRect(LedPlacement placement, ZoneArea area, PixelRect content)
    {
        if (!placement.IsMapped || content.Width <= 0 || content.Height <= 0)
        {
            return default;
        }

        bool horizontal = LedSegment.IsHorizontal(placement.Edge);
        double start = horizontal ? area.X : area.Y;
        double length = horizontal ? area.Width : area.Height;
        double centre = start + placement.Position * length;
        double half = placement.Span * length * (1 + Math.Max(0, area.Overlap)) / 2;

        if (horizontal)
        {
            (int x0, int x1) = Range(centre - half, centre + half, content.X0, content.Width);
            (int y0, int y1) = Range(area.Y, area.Y + area.Height, content.Y0, content.Height);
            return new PixelRect(x0, y0, x1, y1);
        }
        else
        {
            (int y0, int y1) = Range(centre - half, centre + half, content.Y0, content.Height);
            (int x0, int x1) = Range(area.X, area.X + area.Width, content.X0, content.Width);
            return new PixelRect(x0, y0, x1, y1);
        }
    }

    /// <summary>The pixel area an LED averages over with the usual area for its edge.</summary>
    /// <param name="depth">How far into the picture to look, as a fraction of its height.</param>
    /// <param name="overlap">Extra width beyond the LED's own slot (0 = none, 1 = twice as wide).</param>
    public static PixelRect SampleRect(LedPlacement placement, PixelRect content, double depth, double overlap) =>
        content.Width <= 0 || content.Height <= 0
            ? default
            : SampleRect(placement, ZoneArea.Default(placement.Edge, depth, overlap, content.Width / (double)content.Height), content);

    /// <summary>
    /// Keeps a sample rectangle within <paramref name="share"/> of the picture height from its edge
    /// (while the test pattern shows, so the control panel in the middle is never sampled).
    /// </summary>
    public static PixelRect KeepNearEdge(PixelRect rect, ScreenEdge edge, PixelRect content, double share)
    {
        int band = Math.Max(1, (int)Math.Round(share * content.Height));
        PixelRect limited = edge switch
        {
            ScreenEdge.Top => rect with { Y1 = Math.Min(rect.Y1, content.Y0 + band) },
            ScreenEdge.Bottom => rect with { Y0 = Math.Max(rect.Y0, content.Y1 - band) },
            ScreenEdge.Left => rect with { X1 = Math.Min(rect.X1, content.X0 + band) },
            ScreenEdge.Right => rect with { X0 = Math.Max(rect.X0, content.X1 - band) },
            _ => rect,
        };

        // An area drawn away from its edge keeps a one-pixel strip at the band's edge.
        if (limited.Y1 <= limited.Y0)
        {
            limited = edge == ScreenEdge.Top ? limited with { Y0 = limited.Y1 - 1 } : limited with { Y1 = limited.Y0 + 1 };
        }

        if (limited.X1 <= limited.X0)
        {
            limited = edge == ScreenEdge.Left ? limited with { X0 = limited.X1 - 1 } : limited with { X1 = limited.X0 + 1 };
        }

        return limited;
    }

    /// <summary>Pixels [from, to) of a span, kept inside it and at least one pixel wide.</summary>
    private static (int, int) Range(double from, double to, int origin, int length)
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
