using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DxDark.App.Controls;

/// <summary>
/// A number with up/down arrows, for small step-by-step adjustments. Arrow keys and the mouse
/// wheel change it too; typed values are applied on Enter or when leaving the box.
/// </summary>
public partial class NumberBox : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberBox),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberBox)d).UpdateText()));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.MinValue));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.MaxValue));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberBox), new PropertyMetadata(1.0));

    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(NumberBox), new PropertyMetadata("0.#", (d, _) => ((NumberBox)d).UpdateText()));

    public NumberBox()
    {
        InitializeComponent();
        Box.LostKeyboardFocus += (_, _) => Commit();
        Box.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Commit();
                    Box.SelectAll();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    UpdateText();
                    e.Handled = true;
                    break;
                case Key.Up:
                    StepBy(1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    StepBy(-1);
                    e.Handled = true;
                    break;
            }
        };
        PreviewMouseWheel += (_, e) =>
        {
            // Only while typing in the box, so scrolling the panel past it never changes the value.
            if (Box.IsKeyboardFocusWithin)
            {
                StepBy(Math.Sign(e.Delta));
                e.Handled = true;
            }
        };
        Loaded += (_, _) => UpdateText();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    private void OnUp(object sender, RoutedEventArgs e) => StepBy(1);

    private void OnDown(object sender, RoutedEventArgs e) => StepBy(-1);

    private void StepBy(int direction)
    {
        Commit();
        SetClamped(Math.Round((Value + direction * Step) / Step) * Step);
    }

    private void Commit()
    {
        if (double.TryParse(Box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double typed))
        {
            SetClamped(typed);
        }

        UpdateText();
    }

    private void SetClamped(double value)
    {
        Value = Math.Clamp(value, Minimum, Maximum);
        UpdateText();
    }

    private void UpdateText()
    {
        string text = Value.ToString(Format, CultureInfo.InvariantCulture);
        if (Value > 0)
        {
            text = "+" + text;
        }

        if (Box.Text != text)
        {
            Box.Text = text;
        }
    }
}
