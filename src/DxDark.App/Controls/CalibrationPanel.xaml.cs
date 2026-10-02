using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DxDark.App.ViewModels;

namespace DxDark.App.Controls;

/// <summary>The zone cards and LED color settings (DataContext: <see cref="CalibrationViewModel"/>).</summary>
public partial class CalibrationPanel : UserControl
{
    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(CalibrationPanel), new PropertyMetadata(296.0));

    public CalibrationPanel()
    {
        InitializeComponent();
    }

    /// <summary>Width of a zone card; the panel shows as many columns of cards as fit.</summary>
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }

    /// <summary>
    /// Clicking a zone selects it and runs the red dot along it on the strip; clicking the selected
    /// zone again deselects it. Using the controls inside the selected zone keeps it selected.
    /// </summary>
    private void OnZonePressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ZoneViewModel zone } card || DataContext is not CalibrationViewModel vm)
        {
            return;
        }

        if (!zone.IsSelected)
        {
            vm.Select(zone);
        }
        else if (!IsInsideControl(e.OriginalSource as DependencyObject, card))
        {
            vm.ClearSelection();
        }
    }

    /// <summary>
    /// For the windows showing this panel: a click anywhere outside the zone cards deselects the zone.
    /// Call from the window's PreviewMouseLeftButtonDown.
    /// </summary>
    public static void DeselectOnOutsideClick(MouseButtonEventArgs e, CalibrationViewModel vm)
    {
        if (vm.SelectedZone is null)
        {
            return;
        }

        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null; d = ParentOf(d))
        {
            if (d is FrameworkElement { DataContext: ZoneViewModel } or FrameworkContentElement { DataContext: ZoneViewModel })
            {
                return; // inside a zone card
            }
        }

        vm.ClearSelection();
    }

    private static bool IsInsideControl(DependencyObject? element, DependencyObject card)
    {
        for (DependencyObject? d = element; d is not null && d != card; d = ParentOf(d))
        {
            if (d is ButtonBase or ComboBox or TextBox or Slider or NumberBox or SettingSlider)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
