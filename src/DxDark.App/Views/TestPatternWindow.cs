using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DxDark.App.Controls;
using DxDark.Capture;

namespace DxDark.App.Views;

/// <summary>
/// A test pattern filling the synced monitor, kept just behind the control panel. It never takes
/// focus, so clicking it leaves the control panel in front.
/// </summary>
public sealed class TestPatternWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    private readonly CalibrationCanvas _canvas = new();
    private readonly MonitorInfo? _monitor;

    public TestPatternWindow(MonitorInfo? monitor)
    {
        _monitor = monitor;
        Title = "DX Dark test pattern";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Black;
        Content = _canvas;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (monitor is not null)
        {
            Left = monitor.Left;
            Top = monitor.Top;
            Width = monitor.Width;
            Height = monitor.Height;
        }

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, (IntPtr)((long)GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow));
            if (_monitor is not null)
            {
                // Exact physical pixels, whatever the DPI scaling.
                SetWindowPos(hwnd, IntPtr.Zero, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, SwpNoActivate | SwpNoZOrder);
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
        };
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    public void ShowPattern(CalibrationPattern pattern, double speed)
    {
        _canvas.Pattern = pattern;
        _canvas.Speed = speed;
        _canvas.InvalidateVisual();
    }

    /// <summary>Puts the pattern directly behind <paramref name="front"/> in the window stack.</summary>
    public void PlaceBehind(Window front)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr frontHwnd = new WindowInteropHelper(front).Handle;
        if (hwnd != IntPtr.Zero && frontHwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, frontHwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private void OnRendering(object? sender, EventArgs e) => _canvas.InvalidateVisual();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
