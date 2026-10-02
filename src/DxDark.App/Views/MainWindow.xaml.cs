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
    private readonly Action _showTestPattern;
    private bool _previewing;

    public MainWindow(MainViewModel vm, Action showTestPattern)
    {
        InitializeComponent();
        _vm = vm;
        _showTestPattern = showTestPattern;
        DataContext = vm;

        Nav.ItemsSource = new[]
        {
            new NavItem("lighting", "Lighting", ""),
            new NavItem("calibration", "Calibration", ""),
        };
        Nav.SelectedIndex = vm.IsCalibrationPage ? 1 : 0;
        Nav.SelectionChanged += (_, _) => _vm.IsCalibrationPage = Nav.SelectedItem is NavItem { Key: "calibration" };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsCalibrationPage))
            {
                Nav.SelectedIndex = vm.IsCalibrationPage ? 1 : 0;
                UpdateHighlight();
            }
        };
        vm.Calibration.SelectionChanged += UpdateHighlight;

        Surface.SetLayout(vm.Controller.Settings.Layout);
        vm.LayoutReplaced += () => Surface.SetLayout(vm.Controller.Settings.Layout);
        IsVisibleChanged += (_, _) =>
        {
            UpdatePreviewDemand();
            if (!IsVisible)
            {
                _vm.Calibration.ClearSelection(); // the red dot runs only while Calibration can be seen
            }
        };
        StateChanged += (_, _) => UpdatePreviewDemand();
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

    private void OnShowTestPattern(object sender, RoutedEventArgs e) => _showTestPattern();

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
