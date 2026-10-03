using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DxDark.App.ViewModels;
using DxDark.Core.Sync;

namespace DxDark.App.Views;

public sealed record NavItem(string Key, string Title, string Glyph);

/// <summary>
/// The control panel: live view in the middle; on the right, the preset strip (Live) or the zones
/// and LED color (Calibration).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _previewing;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Nav.ItemsSource = new[]
        {
            new NavItem("lighting", "Lighting", ""),
            new NavItem("calibration", "Calibration", ""),
            new NavItem("shortcuts", "Shortcuts", ""),
        };
        Nav.SelectedIndex = NavIndex();
        Nav.SelectionChanged += (_, _) =>
        {
            string key = (Nav.SelectedItem as NavItem)?.Key ?? "lighting";
            _vm.IsCalibrationPage = key == "calibration";
            _vm.IsShortcutsPage = key == "shortcuts";
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsShortcutsPage))
            {
                Nav.SelectedIndex = NavIndex();
            }

            if (e.PropertyName == nameof(MainViewModel.IsCalibrationPage))
            {
                Nav.SelectedIndex = NavIndex();
                UpdateHighlight();
                Surface.AreaProvider = vm.IsCalibrationPage ? vm.Calibration.AreasFor : null;
            }
        };
        vm.Calibration.SelectionChanged += UpdateHighlight;
        vm.Calibration.AreasChanged += Surface.InvalidateVisual;
        Surface.AreaProvider = vm.IsCalibrationPage ? vm.Calibration.AreasFor : null;
        Surface.ZonePressed += zone =>
        {
            if (zone < vm.Calibration.Zones.Count)
            {
                vm.Calibration.Select(vm.Calibration.Zones[zone]);
            }
        };
        Surface.ZoneAreaChanged += vm.Calibration.SetArea;
        Surface.SelectedZoneClicked += _ => vm.Calibration.ClearSelection();
        Surface.EmptyPressed += vm.Calibration.ClearSelection;

        Surface.SetLayout(vm.Controller.Settings.Layout);
        vm.LayoutReplaced += () => Surface.SetLayout(vm.Controller.Settings.Layout);
        IsVisibleChanged += (_, _) =>
        {
            UpdatePreviewDemand();
            if (!IsVisible)
            {
                _vm.Calibration.ClearSelection(); // the red dot runs only while Calibration can be seen
                _vm.Calibration.IsPatternShown = false;
            }
        };
        StateChanged += (_, _) =>
        {
            UpdatePreviewDemand();
            if (WindowState == WindowState.Minimized)
            {
                _vm.Calibration.IsPatternShown = false;
            }
        };
        SizeChanged += (_, _) => SettingsSheet.MaxHeight = Math.Max(300, ActualHeight - 100);
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_vm.IsCalibrationPage)
            {
                Controls.CalibrationPanel.DeselectOnOutsideClick(e, _vm.Calibration);
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _vm.Calibration.IsPatternShown)
            {
                _vm.Calibration.IsPatternShown = false;
                e.Handled = true;
            }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _vm.IsCalibrationPage
                && _vm.Calibration.SelectedZone is not null && !InTextInput())
            {
                // Arrow keys move the selected zone's area: half a percent, or 2 % with Shift.
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.02 : 0.005;
                (double dx, double dy) = e.Key switch
                {
                    Key.Left => (-step, 0.0),
                    Key.Right => (step, 0.0),
                    Key.Up => (0.0, -step),
                    _ => (0.0, step),
                };
                _vm.Calibration.MoveSelectedArea(dx, dy);
                e.Handled = true;
            }
        };
    }

    /// <summary>When false, closing hides the window to the tray instead.</summary>
    public bool AllowClose { get; set; }

    public void ShowSettings()
    {
        _vm.Settings.Refresh();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    public void HideSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;

    public void ShowPage(bool calibration) => _vm.IsCalibrationPage = calibration;

    /// <summary>The live view runs only while the window is visible and not minimised.</summary>
    private void UpdatePreviewDemand()
    {
        bool wanted = IsVisible && WindowState != WindowState.Minimized;
        if (wanted == _previewing)
        {
            return;
        }

        _previewing = wanted;
        if (wanted)
        {
            _vm.PreviewReceived += OnPreview;
        }
        else
        {
            _vm.PreviewReceived -= OnPreview;
        }

        _vm.RequestPreview(this, wanted);
    }

    private void OnPreview(PreviewSnapshot snapshot) => Surface.Update(snapshot);

    private int NavIndex() => _vm.IsCalibrationPage ? 1 : _vm.IsShortcutsPage ? 2 : 0;

    private static bool InTextInput() =>
        Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem or Slider;

    /// <summary>In Calibration, the selected zone is outlined in the live view.</summary>
    private void UpdateHighlight() =>
        Surface.HighlightSegment = _vm.IsCalibrationPage ? _vm.Calibration.SelectedIndex : -1;

    private void OnToggleDetails(object sender, MouseButtonEventArgs e)
    {
        _vm.ShowDetails = !_vm.ShowDetails;
        Chevron.Text = _vm.ShowDetails ? "" : "";
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowSettings();

    private void OnCloseSettings(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void OnOverlayBackgroundClick(object sender, MouseButtonEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void OnSheetClick(object sender, MouseButtonEventArgs e) => e.Handled = true; // clicks inside the sheet keep it open

    private void OnSetUpZones(object sender, RoutedEventArgs e) => ShowPage(calibration: true);


    private void OnPresetMenu(object sender, RoutedEventArgs e)
    {
        if (PresetMenuButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = PresetMenuButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnColorPopupClosed(object? sender, EventArgs e) => CustomColorToggle.IsChecked = false;

    private void OnEffectTileChecked(object sender, RoutedEventArgs e) => (sender as FrameworkElement)?.BringIntoView();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
