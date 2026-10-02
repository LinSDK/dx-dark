using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DxDark.Core.Layout;
using DxDark.Core.Sync;

namespace DxDark.App.Controls;

/// <summary>
/// The live view: the sampled picture with the preset's color processing applied, each LED's
/// sampling zone, and a ring of LED dots in the colors intended for the strip.
/// </summary>
public sealed class PreviewSurface : FrameworkElement
{
    public static readonly DependencyProperty ShowZonesProperty =
        DependencyProperty.Register(nameof(ShowZones), typeof(bool), typeof(PreviewSurface),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Index of the zone to highlight (the one selected in Calibration), or -1.</summary>
    public static readonly DependencyProperty HighlightSegmentProperty =
        DependencyProperty.Register(nameof(HighlightSegment), typeof(int), typeof(PreviewSurface),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush ScreenBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x07, 0x08, 0x0B)));
    private static readonly Pen BezelPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x3C))), 2));
    private static readonly Pen ZonePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(105, 255, 255, 255))), 1));
    private static readonly Pen DimZonePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255))), 1));
    private static readonly Pen HighlightZonePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF))), 1.5));
    private static readonly Pen HighlightRing = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF))), 1.4));
    private static readonly Pen ContentPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x4B))), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen DotOutline = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255))), 0.8));
    private static readonly Brush IdleDot = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0x74, 0x84)));

    private WriteableBitmap? _bitmap;
    private int _frameWidth = 16;
    private int _frameHeight = 9;
    private long _frameSequence = -1;
    private PixelRect _content;
    private PixelRect[] _rects = [];
    private LedPlacement[] _placements = [];
    private byte[] _colors = [];
    private bool _live;
    private bool _sending;

    public PreviewSurface()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public bool ShowZones { get => (bool)GetValue(ShowZonesProperty); set => SetValue(ShowZonesProperty, value); }

    public int HighlightSegment { get => (int)GetValue(HighlightSegmentProperty); set => SetValue(HighlightSegmentProperty, value); }

    /// <summary>LED positions to draw before the first live frame arrives.</summary>
    public void SetLayout(LedLayout layout)
    {
        if (!_live)
        {
            _placements = LayoutGeometry.Placements(layout);
            InvalidateVisual();
        }
    }

    public void Update(PreviewSnapshot snapshot)
    {
        var frame = snapshot.Frame;
        if (frame.Width > 0 && frame.Height > 0 && frame.Sequence != _frameSequence)
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
            _frameSequence = frame.Sequence;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
        }

        _content = snapshot.Content;
        if (_rects.Length != snapshot.SampleRects.Length)
        {
            _rects = new PixelRect[snapshot.SampleRects.Length];
        }

        snapshot.SampleRects.CopyTo(_rects, 0);
        _placements = snapshot.Placements;
        if (_colors.Length != snapshot.LedColors.Length)
        {
            _colors = new byte[snapshot.LedColors.Length];
        }

        snapshot.LedColors.CopyTo(_colors, 0);
        _sending = snapshot.Sending;
        _live = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 60 || h < 60)
        {
            return;
        }

        bool live = _live && _bitmap is not null;
        double aspect = live ? _frameWidth / (double)_frameHeight : 16.0 / 9.0;
        double margin = Math.Clamp(Math.Min(w, h) * 0.07, 18, 40);
        double iw = w - 2 * margin, ih = iw / aspect;
        if (ih > h - 2 * margin)
        {
            ih = h - 2 * margin;
            iw = ih * aspect;
        }

        var image = new Rect((w - iw) / 2, (h - ih) / 2, iw, ih);
        dc.DrawRoundedRectangle(ScreenBrush, BezelPen, Inflate(image, 3), 6, 6);
        if (live)
        {
            dc.DrawImage(_bitmap, image);
            if (ShowZones)
            {
                DrawZones(dc, image);
            }
        }
        else
        {
            var text = new FormattedText("Waiting for the picture…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(image.X + (image.Width - text.Width) / 2, image.Y + (image.Height - text.Height) / 2));
        }

        DrawLeds(dc, image, margin, live);
    }

    private void DrawZones(DrawingContext dc, Rect image)
    {
        double sx = image.Width / _frameWidth, sy = image.Height / _frameHeight;
        int highlight = HighlightSegment;
        for (int pass = 0; pass < 2; pass++) // the highlighted zone on top
        {
            for (int i = 0; i < _rects.Length; i++)
            {
                PixelRect r = _rects[i];
                bool highlighted = highlight >= 0 && i < _placements.Length && _placements[i].Segment == highlight;
                if (r.Area > 0 && highlighted == (pass == 1))
                {
                    Pen pen = highlighted ? HighlightZonePen : highlight >= 0 ? DimZonePen : ZonePen;
                    dc.DrawRectangle(null, pen, new Rect(image.X + r.X0 * sx, image.Y + r.Y0 * sy, r.Width * sx, r.Height * sy));
                }
            }
        }

        bool letterboxed = _content.X0 > 0 || _content.Y0 > 0 || _content.X1 < _frameWidth || _content.Y1 < _frameHeight;
        if (letterboxed && _content.Area > 0)
        {
            dc.DrawRectangle(null, ContentPen, new Rect(image.X + _content.X0 * sx, image.Y + _content.Y0 * sy, _content.Width * sx, _content.Height * sy));
        }
    }

    private void DrawLeds(DrawingContext dc, Rect image, double margin, bool live)
    {
        int count = live ? Math.Min(_colors.Length / 3, _placements.Length) : _placements.Length;
        int highlight = HighlightSegment;
        for (int p = 0; p < count; p++)
        {
            LedPlacement placement = _placements[p];
            if (!placement.IsMapped)
            {
                continue;
            }

            Point center = DotPosition(placement, image, margin);
            double spacing = placement.Span * (LedSegment.IsHorizontal(placement.Edge) ? image.Width : image.Height);
            double radius = Math.Clamp(spacing * 0.42, 1.6, margin * 0.28);
            if (placement.Segment == highlight)
            {
                dc.DrawEllipse(null, HighlightRing, center, radius + 2.2, radius + 2.2);
            }

            if (!live || !_sending)
            {
                // Not sending (off, strip buttons in control, or no strip): show where the LEDs are,
                // faintly tinted with what they would show.
                Brush fill = IdleDot;
                if (live)
                {
                    var faded = new SolidColorBrush(Color.FromArgb(90, _colors[p * 3], _colors[p * 3 + 1], _colors[p * 3 + 2]));
                    faded.Freeze();
                    fill = faded;
                }

                dc.DrawEllipse(fill, DotOutline, center, radius, radius);
                continue;
            }

            var color = Color.FromRgb(_colors[p * 3], _colors[p * 3 + 1], _colors[p * 3 + 2]);
            double level = Math.Max(color.R, Math.Max(color.G, color.B)) / 255.0;
            if (level > 0.04)
            {
                var glow = new RadialGradientBrush(Color.FromArgb((byte)(150 * level), color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
                glow.Freeze();
                dc.DrawEllipse(glow, null, center, radius * 2.6, radius * 2.6);
            }

            var solid = new SolidColorBrush(color);
            solid.Freeze();
            dc.DrawEllipse(solid, DotOutline, center, radius, radius);
        }
    }

    /// <summary>Centre of an LED dot, half of <paramref name="distance"/> outside the screen edge.</summary>
    private static Point DotPosition(LedPlacement placement, Rect image, double distance)
    {
        double d = distance * 0.5;
        double along = Math.Clamp(placement.Position, 0, 1);
        return placement.Edge switch
        {
            ScreenEdge.Top => new Point(image.Left + along * image.Width, image.Top - d),
            ScreenEdge.Bottom => new Point(image.Left + along * image.Width, image.Bottom + d),
            ScreenEdge.Left => new Point(image.Left - d, image.Top + along * image.Height),
            ScreenEdge.Right => new Point(image.Right + d, image.Top + along * image.Height),
            _ => new Point(image.Left + image.Width / 2, image.Bottom + d),
        };
    }

    private static Rect Inflate(Rect r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
