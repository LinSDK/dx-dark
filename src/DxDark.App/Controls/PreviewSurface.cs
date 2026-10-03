using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DxDark.Core.Layout;
using DxDark.Core.Sync;

namespace DxDark.App.Controls;

/// <summary>
/// The live view: the sampled picture with the preset's color processing applied, each LED's
/// sampling zone, and a ring of LED dots in the colors intended for the strip. In Calibration
/// (<see cref="AreaProvider"/> set) it shows each zone's area instead, which can be dragged and
/// resized with the mouse.
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
    private static readonly Pen AreaPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255))), 1.2));
    private static readonly Brush AreaFill = Freeze(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)));
    private static readonly Pen SelectedAreaPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF))), 2));
    private static readonly Brush SelectedAreaFill = Freeze(new SolidColorBrush(Color.FromArgb(48, 0x8B, 0x7C, 0xFF)));
    private static readonly Brush HandleFill = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x7C, 0xFF)));
    private static readonly Pen HandlePen = Freeze(new Pen(Brushes.White, 1));
    private static readonly Brush BadgeFill = Freeze(new SolidColorBrush(Color.FromArgb(200, 0x10, 0x12, 0x17)));
    private const double HandleSize = 8;

    /// <summary>Which part of an area a drag moves.</summary>
    private enum Grip
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private Func<double, IReadOnlyList<ZoneArea>>? _areaProvider;
    private Rect _image;
    private int _dragZone = -1;
    private Grip _dragGrip;
    private Point _dragStart;
    private ZoneArea _dragArea;
    private bool _dragMoved;
    private bool _dragWasSelected;

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

    /// <summary>
    /// Calibration: the area of each zone for a picture of the given width / height. While set, the
    /// areas are drawn and can be edited; null shows each LED's sampling zone instead.
    /// </summary>
    public Func<double, IReadOnlyList<ZoneArea>>? AreaProvider
    {
        get => _areaProvider;
        set
        {
            _areaProvider = value;
            _dragZone = -1;
            Cursor = null;
            InvalidateVisual();
        }
    }

    /// <summary>A zone that was not selected was pressed.</summary>
    public event Action<int>? ZonePressed;

    /// <summary>A zone's area was dragged or resized (raised while dragging).</summary>
    public event Action<int, ZoneArea>? ZoneAreaChanged;

    /// <summary>The selected zone was clicked without being moved.</summary>
    public event Action<int>? SelectedZoneClicked;

    /// <summary>The picture was pressed outside every zone.</summary>
    public event Action? EmptyPressed;

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
        _image = image;
        dc.DrawRoundedRectangle(ScreenBrush, BezelPen, Inflate(image, 3), 6, 6);
        if (live)
        {
            dc.DrawImage(_bitmap, image);
        }

        if (_areaProvider is not null)
        {
            DrawAreas(dc, image);
        }
        else if (live && ShowZones)
        {
            DrawZones(dc, image);
        }

        if (!live)
        {
            var text = new FormattedText("Waiting for the picture…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(image.X + (image.Width - text.Width) / 2, image.Y + (image.Height - text.Height) / 2));
        }

        DrawLeds(dc, image, margin, live);
    }

    // ── Zone areas (Calibration) ────────────────────────────────────────────

    /// <summary>The picture area (inside any black bars) in control coordinates.</summary>
    private Rect PictureOnScreen()
    {
        if (!_live || _content.Area <= 0 || _image.Width <= 0)
        {
            return _image;
        }

        double sx = _image.Width / _frameWidth, sy = _image.Height / _frameHeight;
        return new Rect(_image.X + _content.X0 * sx, _image.Y + _content.Y0 * sy, _content.Width * sx, _content.Height * sy);
    }

    private double PictureAspect() => _live && _content.Area > 0 ? _content.Width / (double)_content.Height : _image.Width / Math.Max(1, _image.Height);

    private IReadOnlyList<ZoneArea> CurrentAreas() => _areaProvider?.Invoke(PictureAspect()) ?? [];

    private static Rect ToScreen(ZoneArea a, Rect picture) =>
        new(picture.X + a.X * picture.Width, picture.Y + a.Y * picture.Height, a.Width * picture.Width, a.Height * picture.Height);

    private void DrawAreas(DrawingContext dc, Rect image)
    {
        Rect picture = PictureOnScreen();
        IReadOnlyList<ZoneArea> areas = CurrentAreas();
        int selected = HighlightSegment;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.PushClip(new RectangleGeometry(Inflate(image, 1)));

        // Each LED's slice of the selected zone, faintly.
        if (_live && selected >= 0)
        {
            double sx = image.Width / _frameWidth, sy = image.Height / _frameHeight;
            for (int i = 0; i < _rects.Length && i < _placements.Length; i++)
            {
                if (_placements[i].Segment == selected && _rects[i].Area > 0)
                {
                    PixelRect r = _rects[i];
                    dc.DrawRectangle(null, DimZonePen, new Rect(image.X + r.X0 * sx, image.Y + r.Y0 * sy, r.Width * sx, r.Height * sy));
                }
            }
        }

        for (int pass = 0; pass < 2; pass++) // the selected zone on top
        {
            for (int zone = 0; zone < areas.Count; zone++)
            {
                if ((zone == selected) != (pass == 1))
                {
                    continue;
                }

                Rect r = ToScreen(areas[zone], picture);
                bool isSelected = zone == selected;
                dc.DrawRectangle(isSelected ? SelectedAreaFill : AreaFill, isSelected ? SelectedAreaPen : AreaPen, r);

                Rect visible = Rect.Intersect(r, image);
                if (!visible.IsEmpty)
                {
                    var number = new FormattedText((zone + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, Brushes.White, pixelsPerDip);
                    var centre = new Point(visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
                    double radius = 10;
                    dc.DrawEllipse(isSelected ? HandleFill : BadgeFill, null, centre, radius, radius);
                    dc.DrawText(number, new Point(centre.X - number.Width / 2, centre.Y - number.Height / 2));
                }

                if (isSelected)
                {
                    foreach ((Grip _, Point p) in Grips(r))
                    {
                        dc.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
                    }
                }
            }
        }

        dc.Pop();
    }

    private static IEnumerable<(Grip, Point)> Grips(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return (Grip.TopLeft, r.TopLeft);
        yield return (Grip.Top, new Point(cx, r.Top));
        yield return (Grip.TopRight, r.TopRight);
        yield return (Grip.Right, new Point(r.Right, cy));
        yield return (Grip.BottomRight, r.BottomRight);
        yield return (Grip.Bottom, new Point(cx, r.Bottom));
        yield return (Grip.BottomLeft, r.BottomLeft);
        yield return (Grip.Left, new Point(r.Left, cy));
    }

    /// <summary>The zone and grip under <paramref name="p"/>: the selected zone's handles first, then the zones from the top.</summary>
    private (int Zone, Grip Grip) HitTest(Point p, IReadOnlyList<ZoneArea> areas)
    {
        Rect picture = PictureOnScreen();
        int selected = HighlightSegment;
        if (selected >= 0 && selected < areas.Count)
        {
            Rect r = ToScreen(areas[selected], picture);
            foreach ((Grip grip, Point at) in Grips(r))
            {
                if (Math.Abs(p.X - at.X) <= HandleSize && Math.Abs(p.Y - at.Y) <= HandleSize)
                {
                    return (selected, grip);
                }
            }

            if (Inflate(r, 2).Contains(p))
            {
                return (selected, Grip.Move);
            }
        }

        for (int zone = areas.Count - 1; zone >= 0; zone--)
        {
            if (zone != selected && Inflate(ToScreen(areas[zone], picture), 2).Contains(p))
            {
                return (zone, Grip.Move);
            }
        }

        return (-1, Grip.None);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_areaProvider is null)
        {
            return;
        }

        IReadOnlyList<ZoneArea> areas = CurrentAreas();
        Point p = e.GetPosition(this);
        (int zone, Grip grip) = HitTest(p, areas);
        if (zone < 0)
        {
            EmptyPressed?.Invoke();
            return;
        }

        _dragWasSelected = zone == HighlightSegment;
        if (!_dragWasSelected)
        {
            ZonePressed?.Invoke(zone);
        }

        _dragZone = zone;
        _dragGrip = grip;
        _dragStart = p;
        _dragArea = areas[zone];
        _dragMoved = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_areaProvider is null)
        {
            return;
        }

        Point p = e.GetPosition(this);
        if (_dragZone < 0 || !IsMouseCaptured)
        {
            (int zone, Grip grip) = HitTest(p, CurrentAreas());
            Cursor = zone < 0 ? null : CursorFor(zone == HighlightSegment ? grip : Grip.Move);
            return;
        }

        if (!_dragMoved && (p - _dragStart).Length < 3)
        {
            return;
        }

        _dragMoved = true;
        Rect picture = PictureOnScreen();
        double dx = (p.X - _dragStart.X) / Math.Max(1, picture.Width);
        double dy = (p.Y - _dragStart.Y) / Math.Max(1, picture.Height);
        ZoneAreaChanged?.Invoke(_dragZone, Drag(_dragArea, _dragGrip, dx, dy));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragZone < 0)
        {
            return;
        }

        int zone = _dragZone;
        _dragZone = -1;
        ReleaseMouseCapture();
        if (!_dragMoved && _dragWasSelected)
        {
            SelectedZoneClicked?.Invoke(zone);
        }
    }

    /// <summary>The area after dragging <paramref name="grip"/> by (dx, dy) picture fractions.</summary>
    private static ZoneArea Drag(ZoneArea a, Grip grip, double dx, double dy)
    {
        double min = ZoneArea.MinSize;
        double left = a.X, top = a.Y, right = a.X + a.Width, bottom = a.Y + a.Height;
        switch (grip)
        {
            case Grip.Move:
                return (a with { X = a.X + dx, Y = a.Y + dy }).Clamped();
            case Grip.Left or Grip.TopLeft or Grip.BottomLeft:
                left = Math.Min(left + dx, right - min);
                break;
            case Grip.Right or Grip.TopRight or Grip.BottomRight:
                right = Math.Max(right + dx, left + min);
                break;
        }

        switch (grip)
        {
            case Grip.Top or Grip.TopLeft or Grip.TopRight:
                top = Math.Min(top + dy, bottom - min);
                break;
            case Grip.Bottom or Grip.BottomLeft or Grip.BottomRight:
                bottom = Math.Max(bottom + dy, top + min);
                break;
        }

        return (a with { X = left, Y = top, Width = right - left, Height = bottom - top }).Clamped();
    }

    private static Cursor CursorFor(Grip grip) => grip switch
    {
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.SizeAll,
    };

    // ── Sampling zones (Lighting) ───────────────────────────────────────────

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
