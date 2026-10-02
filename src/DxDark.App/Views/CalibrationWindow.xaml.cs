using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DxDark.App.ViewModels;
using DxDark.Capture;
using DxDark.Core;

namespace DxDark.App.Views;

/// <summary>
/// The optional full-screen test pattern on the synced monitor, with the zone and LED color
/// controls floating in the middle. Screen sync runs the whole time, sampling only near the edges,
/// so every change shows on the strip immediately.
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly LightController _controller;
    private readonly CalibrationViewModel _vm;
    private readonly MonitorInfo? _monitor;
    private readonly bool _live;

    /// <param name="live">False only when rendering a still image (no strip, no animation).</param>
    public CalibrationWindow(LightController controller, CalibrationViewModel vm, bool live = true)
    {
        InitializeComponent();
        _controller = controller;
        _vm = vm;
        _live = live;
        DataContext = vm;
        _monitor = live ? FindMonitor(controller.Settings.MonitorDeviceName) : null;
        if (_monitor is not null)
        {
            Left = _monitor.Left;
            Top = _monitor.Top;
            Width = _monitor.Width;
            Height = _monitor.Height;
        }

        vm.PatternChanged += ApplyPattern;
        ApplyPattern();

        SourceInitialized += (_, _) => FitToMonitor();
        Loaded += (_, _) =>
        {
            FitToMonitor();
            Activate();
            if (_live)
            {
                _controller.BeginCalibration();
                CompositionTarget.Rendering += OnRendering;
            }
        };
        Closed += (_, _) =>
        {
            vm.PatternChanged -= ApplyPattern;
            if (_live)
            {
                CompositionTarget.Rendering -= OnRendering;
                _controller.EndCalibration();
            }
        };
        PreviewMouseLeftButtonDown += (_, e) => Controls.CalibrationPanel.DeselectOnOutsideClick(e, vm);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        SizeChanged += (_, _) => Panel.MaxHeight = Math.Max(400, ActualHeight * 0.68);
    }

    /// <summary>For still images: shows the pattern at a fixed moment.</summary>
    public void FreezeAt(double seconds) => Canvas.FixedTime = seconds;

    private void ApplyPattern()
    {
        Canvas.Pattern = _vm.SelectedPattern.Pattern;
        Canvas.Speed = _vm.PatternSpeed;
        Canvas.InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e) => Canvas.InvalidateVisual();

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    /// <summary>Covers the whole monitor in physical pixels, independent of DPI scaling.</summary>
    private void FitToMonitor()
    {
        if (!_live)
        {
            return;
        }

        if (_monitor is null)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, new IntPtr(-1), _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, 0x0040);
        }
    }

    private static MonitorInfo? FindMonitor(string? deviceName)
    {
        try
        {
            List<MonitorInfo> monitors = MonitorEnumerator.List();
            return monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(m => m.IsPrimary)
                ?? monitors.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn($"Calibration: could not list monitors ({ex.Message})");
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
