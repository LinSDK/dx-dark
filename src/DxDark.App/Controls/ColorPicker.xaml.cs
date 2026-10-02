using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DxDark.Core.Color;

namespace DxDark.App.Controls;

/// <summary>Hue / saturation / brightness picker with a hex field. <see cref="Hex"/> is "#RRGGBB".</summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty HexProperty =
        DependencyProperty.Register(nameof(Hex), typeof(string), typeof(ColorPicker),
            new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

    private bool _updating;
    private string? _lastHexFromSliders;

    public ColorPicker()
    {
        InitializeComponent();
        HueSlider.ValueChanged += (_, _) => FromSliders();
        SaturationSlider.ValueChanged += (_, _) => FromSliders();
        ValueSlider.ValueChanged += (_, _) => FromSliders();
        HexBox.LostKeyboardFocus += (_, _) => FromHexBox();
        HexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FromHexBox();
            }
        };
        Loaded += (_, _) => FromHex(Hex);
    }

    public string Hex { get => (string)GetValue(HexProperty); set => SetValue(HexProperty, value); }

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        string hex = (string)e.NewValue;

        // Ignore our own value coming back through the binding: re-deriving hue from the rounded
        // RGB value would nudge the slider the user is dragging.
        if (!picker._updating && !string.Equals(hex, picker._lastHexFromSliders, StringComparison.OrdinalIgnoreCase))
        {
            picker.FromHex(hex);
        }
    }

    private void FromHex(string? hex)
    {
        if (!ColorMath.TryParseHex(hex, out byte r, out byte g, out byte b))
        {
            return;
        }

        (double h, double s, double v) = ToHsv(r, g, b);
        _updating = true;
        try
        {
            // Keep the hue when the color is gray or black, so the sliders do not jump.
            if (s > 0 && v > 0)
            {
                HueSlider.Value = h;
            }

            if (v > 0)
            {
                SaturationSlider.Value = s;
            }

            ValueSlider.Value = v;
            HexBox.Text = ColorMath.ToHex(r, g, b);
        }
        finally
        {
            _updating = false;
        }

        UpdateVisuals(r, g, b);
    }

    private void FromSliders()
    {
        if (_updating)
        {
            return;
        }

        (byte r, byte g, byte b) = FromHsv(HueSlider.Value, SaturationSlider.Value, ValueSlider.Value);
        string hex = ColorMath.ToHex(r, g, b);
        _lastHexFromSliders = hex;
        _updating = true;
        try
        {
            HexBox.Text = hex;
            Hex = hex;
        }
        finally
        {
            _updating = false;
        }

        UpdateVisuals(r, g, b);
    }

    private void FromHexBox()
    {
        string text = HexBox.Text.Trim();
        if (ColorMath.TryParseHex(text, out byte r, out byte g, out byte b))
        {
            Hex = ColorMath.ToHex(r, g, b);
            FromHex(Hex);
        }
        else
        {
            HexBox.Text = Hex;
        }
    }

    private void UpdateVisuals(byte r, byte g, byte b)
    {
        Preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        (byte hr, byte hg, byte hb) = FromHsv(HueSlider.Value, 1, 1);
        (byte sr, byte sg, byte sb) = FromHsv(HueSlider.Value, SaturationSlider.Value, 1);
        SaturationBar.Background = new LinearGradientBrush(Colors.White, Color.FromRgb(hr, hg, hb), 0);
        ValueBar.Background = new LinearGradientBrush(Colors.Black, Color.FromRgb(sr, sg, sb), 0);
    }

    internal static (double H, double S, double V) ToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            h = max == r ? 60 * (((g - b) / delta) % 6)
                : max == g ? 60 * ((b - r) / delta + 2)
                : 60 * ((r - g) / delta + 4);
        }

        return (h < 0 ? h + 360 : h, max > 0 ? delta / max : 0, max);
    }

    internal static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
