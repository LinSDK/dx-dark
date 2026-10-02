using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DxDark.App.Controls;

/// <summary>
/// Label, slider, editable value and a reset button in one row. <see cref="Value"/> is stored in
/// model units; <see cref="Scale"/> converts it for display (e.g. 0.12 shown as "12 %").
/// </summary>
public partial class SettingSlider : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingSlider), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingSlider), new PropertyMetadata(null));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(SettingSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SettingSlider), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SettingSlider), new PropertyMetadata(1.0));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(SettingSlider), new PropertyMetadata(0.01));

    public static readonly DependencyProperty ScaleProperty =
        DependencyProperty.Register(nameof(Scale), typeof(double), typeof(SettingSlider), new PropertyMetadata(1.0, OnDisplayChanged));

    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(SettingSlider), new PropertyMetadata("0", OnDisplayChanged));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(SettingSlider), new PropertyMetadata("", OnDisplayChanged));

    public static readonly DependencyProperty DefaultValueProperty =
        DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(SettingSlider), new PropertyMetadata(double.NaN, OnDisplayChanged));

    public static readonly DependencyProperty ValueWidthProperty =
        DependencyProperty.Register(nameof(ValueWidth), typeof(double), typeof(SettingSlider), new PropertyMetadata(76.0));

    private bool _syncing;

    public SettingSlider()
    {
        InitializeComponent();
        Track.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                Value = e.NewValue;
            }
        };
        ValueBox.LostKeyboardFocus += (_, _) => CommitText();
        ValueBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                Keyboard.ClearFocus();
            }
            else if (e.Key == Key.Escape)
            {
                UpdateDisplay();
                Keyboard.ClearFocus();
            }
        };
        LabelText.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ResetToDefault();
            }
        };
        Loaded += (_, _) => UpdateDisplay();
    }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }

    public double ValueWidth { get => (double)GetValue(ValueWidthProperty); set => SetValue(ValueWidthProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingSlider)d).UpdateDisplay();

    private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingSlider)d).UpdateDisplay();

    private void UpdateDisplay()
    {
        _syncing = true;
        try
        {
            Track.Value = Value;
        }
        finally
        {
            _syncing = false;
        }

        if (!ValueBox.IsKeyboardFocusWithin)
        {
            string number = (Value * Scale).ToString(Format, CultureInfo.CurrentCulture);
            ValueBox.Text = string.IsNullOrEmpty(Unit) ? number : $"{number} {Unit}";
        }

        bool atDefault = double.IsNaN(DefaultValue) || Math.Abs(Value - DefaultValue) < Math.Max(1e-9, Step / 2);
        ResetButton.Visibility = atDefault ? Visibility.Hidden : Visibility.Visible;
    }

    private void CommitText()
    {
        string text = ValueBox.Text.Replace(Unit, "", StringComparison.Ordinal).Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double shown) && Scale != 0)
        {
            Value = Math.Clamp(shown / Scale, Minimum, Maximum);
        }

        UpdateDisplay();
    }

    private void ResetToDefault()
    {
        if (!double.IsNaN(DefaultValue))
        {
            Value = DefaultValue;
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ResetToDefault();
}
