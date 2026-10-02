namespace DxDark.Core.Layout;

public enum StartCorner
{
    BottomLeft,
    TopLeft,
    TopRight,
    BottomRight,
}

/// <summary>Builds a layout from per-side LED counts, the corner where LED 1 sits and the direction.</summary>
public static class QuickLayout
{
    /// <param name="clockwise">Direction as seen from the front of the screen.</param>
    /// <param name="top">LEDs along the top edge (0 if none); likewise for the other sides.</param>
    public static LedLayout Create(StartCorner start, bool clockwise, int top, int right, int bottom, int left)
    {
        // Sides visited going clockwise from the bottom-left corner, and counter-clockwise likewise.
        ScreenEdge[] cw = [ScreenEdge.Left, ScreenEdge.Top, ScreenEdge.Right, ScreenEdge.Bottom];
        ScreenEdge[] ccw = [ScreenEdge.Bottom, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Left];
        int first = (start, clockwise) switch
        {
            (StartCorner.BottomLeft, true) => 0,
            (StartCorner.TopLeft, true) => 1,
            (StartCorner.TopRight, true) => 2,
            (StartCorner.BottomRight, true) => 3,
            (StartCorner.BottomLeft, false) => 0,
            (StartCorner.BottomRight, false) => 1,
            (StartCorner.TopRight, false) => 2,
            _ => 3, // top-left, counter-clockwise
        };

        ScreenEdge[] order = clockwise ? cw : ccw;
        var layout = new LedLayout();
        for (int i = 0; i < 4; i++)
        {
            ScreenEdge edge = order[(first + i) % 4];
            int count = edge switch
            {
                ScreenEdge.Top => top,
                ScreenEdge.Right => right,
                ScreenEdge.Bottom => bottom,
                _ => left,
            };
            if (count <= 0)
            {
                continue;
            }

            // Clockwise runs up the left side, left→right along the top, down the right side and
            // right→left along the bottom; "Reversed" is relative to left→right / top→bottom.
            bool reversedWhenClockwise = edge is ScreenEdge.Left or ScreenEdge.Bottom;
            layout.Segments.Add(new LedSegment
            {
                Edge = edge,
                LedCount = count,
                Reversed = clockwise ? reversedWhenClockwise : !reversedWhenClockwise,
            });
        }

        return layout;
    }

    /// <summary>DX Light's default wiring: LED 1 at the bottom-left, running clockwise around all four sides.</summary>
    public static LedLayout FourSided(int horizontal, int vertical) =>
        Create(StartCorner.BottomLeft, clockwise: true, horizontal, vertical, horizontal, vertical);
}
