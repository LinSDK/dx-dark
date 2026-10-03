using DxDark.Capture;
using DxDark.Core.Layout;

namespace DxDark.Core.Imaging;

/// <summary>
/// Finds letterbox (top/bottom) and pillarbox (left/right) black bars so LEDs sample the picture,
/// not the bars. Bars are assumed symmetric, which avoids mistaking a dark scene edge for a bar.
/// Growing bars must be stable for a second, and need a lit picture next to them; dark or fading
/// frames never remove bars that are already found, so a movie fading to black and back keeps
/// them. Shrinking applies almost at once so bright content entering the bar area is never missed.
/// </summary>
public sealed class BlackBarDetector
{
    private const int DarkLevel = 24;          // sRGB value; letterbox black is normally 0–16
    private const double MaxBarFraction = 0.35;
    private const double GrowDelay = 1.0;      // seconds
    private const double ShrinkDelay = 0.15;
    private const double MinEdgeLit = 0.3;     // share of the first row/column inside bars that must be lit

    private Axis _vertical = new();
    private Axis _horizontal = new();

    public void Reset()
    {
        _vertical = new Axis();
        _horizontal = new Axis();
    }

    /// <returns>The picture area of <paramref name="frame"/>.</returns>
    public PixelRect Update(CapturedFrame frame, double timeSeconds, bool enabled)
    {
        int w = frame.Width, h = frame.Height;
        if (!enabled || w < 8 || h < 8)
        {
            Reset();
            return new PixelRect(0, 0, w, h);
        }

        int maxRows = (int)(h * MaxBarFraction);
        int maxCols = (int)(w * MaxBarFraction);
        int top = CountDarkRows(frame, fromTop: true, maxRows);
        int bottom = CountDarkRows(frame, fromTop: false, maxRows);
        int left = CountDarkColumns(frame, fromLeft: true, maxCols);
        int right = CountDarkColumns(frame, fromLeft: false, maxCols);

        // A (nearly) black frame says nothing about bars; keep what we have.
        bool blackFrame = top >= maxRows && bottom >= maxRows;
        if (!blackFrame)
        {
            int rows = Math.Min(top, bottom), columns = Math.Min(left, right);

            // Bigger bars than before only count when a lit picture borders them: a dim or fading
            // scene, or a small bright object on a dark screen (e.g. a test pattern), is no evidence.
            if (rows <= _vertical.Applied + 1
                || (rows < maxRows && LitFraction(frame, row: rows) >= MinEdgeLit && LitFraction(frame, row: h - 1 - rows) >= MinEdgeLit))
            {
                _vertical.Update(rows, timeSeconds);
            }

            if (columns <= _horizontal.Applied + 1
                || (columns < maxCols && LitFraction(frame, column: columns) >= MinEdgeLit && LitFraction(frame, column: w - 1 - columns) >= MinEdgeLit))
            {
                _horizontal.Update(columns, timeSeconds);
            }
        }

        int v = Math.Min(_vertical.Applied, maxRows);
        int hz = Math.Min(_horizontal.Applied, maxCols);
        return new PixelRect(hz, v, w - hz, h - v);
    }

    private static int CountDarkRows(CapturedFrame frame, bool fromTop, int max)
    {
        int w = frame.Width, h = frame.Height;
        int allowed = Math.Max(1, w / 100); // tolerate a few stray pixels (logos, noise)
        for (int n = 0; n < max; n++)
        {
            int y = fromTop ? n : h - 1 - n;
            int bright = 0;
            int i = y * w * 4;
            for (int x = 0; x < w; x++, i += 4)
            {
                if (IsBright(frame.Pixels, i) && ++bright > allowed)
                {
                    return n;
                }
            }
        }

        return max;
    }

    private static int CountDarkColumns(CapturedFrame frame, bool fromLeft, int max)
    {
        int w = frame.Width, h = frame.Height;
        int allowed = Math.Max(1, h / 100);
        for (int n = 0; n < max; n++)
        {
            int x = fromLeft ? n : w - 1 - n;
            int bright = 0;
            for (int y = 0; y < h; y++)
            {
                if (IsBright(frame.Pixels, (y * w + x) * 4) && ++bright > allowed)
                {
                    return n;
                }
            }
        }

        return max;
    }

    /// <summary>Share of non-dark pixels in one row or one column.</summary>
    private static double LitFraction(CapturedFrame frame, int row = -1, int column = -1)
    {
        int w = frame.Width, h = frame.Height, lit = 0;
        if (row >= 0)
        {
            for (int x = 0, i = row * w * 4; x < w; x++, i += 4)
            {
                lit += IsBright(frame.Pixels, i) ? 1 : 0;
            }

            return lit / (double)w;
        }

        for (int y = 0; y < h; y++)
        {
            lit += IsBright(frame.Pixels, (y * w + column) * 4) ? 1 : 0;
        }

        return lit / (double)h;
    }

    private static bool IsBright(byte[] px, int i) =>
        px[i] > DarkLevel || px[i + 1] > DarkLevel || px[i + 2] > DarkLevel;

    private sealed class Axis
    {
        private int _candidate = -1;
        private double _candidateSince;

        public int Applied { get; private set; }

        public void Update(int measured, double now)
        {
            if (Math.Abs(measured - Applied) <= 1)
            {
                _candidate = -1;
                return;
            }

            if (_candidate < 0 || Math.Abs(measured - _candidate) > 2)
            {
                _candidate = measured;
                _candidateSince = now;
                return;
            }

            double delay = measured < Applied ? ShrinkDelay : GrowDelay;
            if (now - _candidateSince >= delay)
            {
                Applied = Math.Min(measured, _candidate);
                _candidate = -1;
            }
        }
    }
}
