using System.Windows;
using System.Windows.Media;

namespace DxDark.App.Controls;

public enum CalibrationPattern
{
    SpinningCross,
    ColoredArms,
    EdgeColors,
    BorderChase,
    ColorCycle,
    White,
    RainbowRing,
}

/// <summary>
/// Full-screen test patterns, shown behind the control panel. The strip samples only the screen
/// edges, so the control panel can sit in the middle without being picked up.
/// </summary>
public sealed class CalibrationCanvas : FrameworkElement
{
    private static readonly Color[] ArmColors =
    [
        Color.FromRgb(255, 40, 40), Color.FromRgb(40, 230, 70), Color.FromRgb(50, 110, 255), Color.FromRgb(255, 215, 0),
    ];

    private DateTime _lastFrame = DateTime.UtcNow;
    private double _phase;

    public CalibrationPattern Pattern { get; set; }

    /// <summary>Animation speed multiplier.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>For rendering stills: a fixed animation time in seconds instead of the clock.</summary>
    public double? FixedTime { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
        if (w < 10 || h < 10)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        _phase += (now - _lastFrame).TotalSeconds * Speed;
        _lastFrame = now;
        double t = FixedTime ?? _phase;
        var center = new Point(w / 2, h / 2);

        switch (Pattern)
        {
            case CalibrationPattern.SpinningCross:
                DrawCross(dc, center, w, h, t, HueColor(t * 0.08));
                break;
            case CalibrationPattern.ColoredArms:
                DrawCross(dc, center, w, h, t, null);
                dc.DrawEllipse(Brushes.Black, null, center, h * 0.13, h * 0.13);
                break;
            case CalibrationPattern.EdgeColors:
                DrawEdges(dc, w, h);
                break;
            case CalibrationPattern.BorderChase:
                DrawChase(dc, w, h, t);
                break;
            case CalibrationPattern.ColorCycle:
                dc.DrawRectangle(new SolidColorBrush(HueColor(t * 0.1)), null, new Rect(0, 0, w, h));
                break;
            case CalibrationPattern.White:
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                break;
            case CalibrationPattern.RainbowRing:
                DrawRainbowRing(dc, center, w, h, t);
                break;
        }
    }

    /// <summary>A cross whose arms reach past the screen edges, turning about the centre.</summary>
    private static void DrawCross(DrawingContext dc, Point center, double w, double h, double t, Color? single)
    {
        double angle = t * 25 % 360;
        double thickness = h * 0.09;
        double reach = Math.Sqrt(w * w + h * h) / 2 + thickness;
        dc.PushTransform(new RotateTransform(angle, center.X, center.Y));
        for (int arm = 0; arm < 4; arm++)
        {
            var brush = new SolidColorBrush(single ?? ArmColors[arm]);
            brush.Freeze();
            dc.PushTransform(new RotateTransform(arm * 90, center.X, center.Y));
            dc.DrawRectangle(brush, null, new Rect(center.X, center.Y - thickness / 2, reach, thickness));
            dc.Pop();
        }

        dc.Pop();
    }

    /// <summary>One color per edge: left red, top green, right blue, bottom yellow.</summary>
    private static void DrawEdges(DrawingContext dc, double w, double h)
    {
        var c = new Point(w / 2, h / 2);
        Triangle(dc, new Point(0, 0), new Point(0, h), c, Color.FromRgb(240, 50, 50));
        Triangle(dc, new Point(0, 0), new Point(w, 0), c, Color.FromRgb(40, 210, 80));
        Triangle(dc, new Point(w, 0), new Point(w, h), c, Color.FromRgb(50, 110, 255));
        Triangle(dc, new Point(0, h), new Point(w, h), c, Color.FromRgb(240, 200, 40));
    }

    private static void Triangle(DrawingContext dc, Point a, Point b, Point c, Color color)
    {
        var geometry = new PathGeometry([new PathFigure(a, [new LineSegment(b, true), new LineSegment(c, true)], true)]);
        geometry.Freeze();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>A bright block running clockwise along the border, starting at the top-left.</summary>
    private static void DrawChase(DrawingContext dc, double w, double h, double t)
    {
        double perimeter = 2 * (w + h);
        double size = Math.Min(w, h) * 0.14;
        double depth = h * 0.12;
        double s = (t * 0.12 % 1) * perimeter;
        Rect block = s < w ? new Rect(s - size / 2, 0, size, depth)
            : s < w + h ? new Rect(w - depth, s - w - size / 2, depth, size)
            : s < 2 * w + h ? new Rect(w - (s - w - h) - size / 2, h - depth, size, depth)
            : new Rect(0, h - (s - 2 * w - h) - size / 2, depth, size);
        var brush = new SolidColorBrush(HueColor(t * 0.06)); // the block slowly runs through every color
        brush.Freeze();
        dc.DrawRectangle(brush, null, block);
    }

    /// <summary>Every hue around the screen, slowly turning: shows that zones join up at the corners.</summary>
    private static void DrawRainbowRing(DrawingContext dc, Point center, double w, double h, double t)
    {
        const int Wedges = 180;
        double reach = Math.Sqrt(w * w + h * h);
        for (int i = 0; i < Wedges; i++)
        {
            double a0 = 2 * Math.PI * i / Wedges, a1 = 2 * Math.PI * (i + 1.05) / Wedges;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(center, true, true);
                g.LineTo(new Point(center.X + reach * Math.Cos(a0), center.Y + reach * Math.Sin(a0)), false, false);
                g.LineTo(new Point(center.X + reach * Math.Cos(a1), center.Y + reach * Math.Sin(a1)), false, false);
            }

            geometry.Freeze();
            var brush = new SolidColorBrush(HueColor((double)i / Wedges + t * 0.05));
            brush.Freeze();
            dc.DrawGeometry(brush, null, geometry);
        }
    }

    private static Color HueColor(double hue)
    {
        hue = (hue % 1 + 1) % 1;
        double r = Math.Clamp(Math.Abs(hue * 6 - 3) - 1, 0, 1);
        double g = Math.Clamp(2 - Math.Abs(hue * 6 - 2), 0, 1);
        double b = Math.Clamp(2 - Math.Abs(hue * 6 - 4), 0, 1);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
