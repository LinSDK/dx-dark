using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DxDark.App.Controls;
using DxDark.App.Infrastructure;
using DxDark.App.Tray;
using DxDark.App.ViewModels;
using DxDark.App.Views;
using DxDark.Capture;
using DxDark.Core;
using DxDark.Core.Effects;
using DxDark.Core.Layout;
using DxDark.Core.Platform;
using DxDark.Core.Settings;

namespace DxDark.App;

public partial class App : Application
{
    private const string InstanceMutexName = "DxDark.SingleInstance";
    private const string ShowEventName = "DxDark.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private SettingsStore? _store;
    private LightController? _controller;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private SystemMonitor? _system;
    private MainWindow? _window;
    private TestPatternWindow? _testPattern;
    private Rect? _boundsBeforePattern;
    private bool _warnedAboutZones;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        int snapshotIndex = Array.IndexOf(e.Args, "--snapshot");
        if (snapshotIndex >= 0)
        {
            string folder = snapshotIndex + 1 < e.Args.Length ? e.Args[snapshotIndex + 1] : Path.Combine(Path.GetTempPath(), "dxdark-snapshots");
            RenderSnapshots(folder);
            return;
        }

        // One instance only; starting it again just opens the control panel.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() => ShowMainWindow());
            }
        })
        { IsBackground = true, Name = "Second instance listener" }.Start();

        _store = new SettingsStore(SettingsStore.DefaultDirectory);
        Log.Initialize(_store.Directory);
        _store.Load();
        _store.ChangedOnDisk += settings => Dispatcher.BeginInvoke(() => ApplyEditedSettings(settings));
        _store.StartWatching();

        _controller = new LightController(_store);
        _vm = new MainViewModel(_controller, Dispatcher, demoMode: false);
        _tray = new TrayIcon(_vm, Dispatcher, () => ShowMainWindow(), OpenCalibration, ExitApp);
        _vm.Calibration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalibrationViewModel.IsPatternShown))
            {
                UpdateTestPattern();
            }
        };
        _vm.Calibration.PatternChanged += () => _testPattern?.ShowPattern(_vm.Calibration.SelectedPattern.Pattern, _vm.Calibration.PatternSpeed);
        _system = new SystemMonitor(_controller);
        _controller.Notice += message => Dispatcher.BeginInvoke(() => _tray?.ShowNotice(message));
        _controller.Start();

        SyncStartWithWindows();

        // On first launch, go straight to Calibration to add the zones.
        bool noZones = _controller.Settings.Layout.Segments.Count == 0;
        bool minimized = e.Args.Contains(StartupShortcut.MinimizedArgument);
        if (!minimized || _store.IsNew)
        {
            ShowMainWindow(calibration: noZones);
        }
        else
        {
            _tray.ShowNotice(noZones ? "DX Dark is running, but no zones have been set. Open Calibration to add them." : "DX Dark is running in the tray.");
        }

        SessionEnding += (_, _) => ExitApp();
    }

    private void ShowMainWindow(bool calibration = false)
    {
        if (_vm is null || _exiting)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow(_vm);
            _window.Activated += (_, _) => _testPattern?.PlaceBehind(_window);
            _window.IsVisibleChanged += (_, _) =>
            {
                // Closing the control panel without any zones: a reminder from the tray, once.
                if (_window is { IsVisible: false } && !_exiting && !_warnedAboutZones && _vm.HasNoZones)
                {
                    _warnedAboutZones = true;
                    _tray?.ShowNotice("No zones have been set, so the strip stays dark. Open Calibration to add them.");
                }
            };
        }

        if (calibration)
        {
            _window.ShowPage(calibration: true);
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _vm.RefreshState();
    }

    private void OpenCalibration() => ShowMainWindow(calibration: true);

    /// <summary>Shows or hides the test pattern behind the control panel (Calibration → Test pattern).</summary>
    private void UpdateTestPattern()
    {
        if (_controller is null || _vm is null)
        {
            return;
        }

        bool wanted = _vm.Calibration.IsPatternShown && !_exiting && _window is { IsVisible: true };
        if (wanted && _testPattern is null)
        {
            MonitorInfo? monitor = SyncedMonitor();
            _testPattern = new TestPatternWindow(monitor);
            _testPattern.ShowPattern(_vm.Calibration.SelectedPattern.Pattern, _vm.Calibration.PatternSpeed);
            _testPattern.Show();
            _controller.BeginCalibration();
            KeepWindowInsideEdges(monitor);
            _testPattern.PlaceBehind(_window!);
            _window!.Activate();
        }
        else if (!wanted && _testPattern is not null)
        {
            _testPattern.Close();
            _testPattern = null;
            _controller.EndCalibration();
            if (_boundsBeforePattern is { } bounds && _window is not null)
            {
                _window.Left = bounds.X;
                _window.Top = bounds.Y;
                _window.Width = bounds.Width;
                _window.Height = bounds.Height;
            }

            _boundsBeforePattern = null;
        }
    }

    /// <summary>
    /// The strip samples the outer 15 % of the screen while the pattern shows, so the control panel
    /// must stay inside that border: it is restored from maximized, and moved (and shrunk if needed)
    /// to the middle of the synced monitor. Its old place is restored when the pattern goes.
    /// </summary>
    private void KeepWindowInsideEdges(MonitorInfo? monitor)
    {
        if (_window is null || monitor is null || PresentationSource.FromVisual(_window) is not { CompositionTarget: { } target })
        {
            return;
        }

        Matrix fromDevice = target.TransformFromDevice;
        Point topLeft = fromDevice.Transform(new Point(monitor.Left, monitor.Top));
        Point bottomRight = fromDevice.Transform(new Point(monitor.Left + monitor.Width, monitor.Top + monitor.Height));
        var screen = new Rect(topLeft, bottomRight);
        double border = screen.Height * 0.17;
        var inside = new Rect(screen.X + border, screen.Y + border, Math.Max(0, screen.Width - 2 * border), Math.Max(0, screen.Height - 2 * border));

        if (_window.WindowState == WindowState.Maximized)
        {
            _window.WindowState = WindowState.Normal;
        }

        var current = new Rect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
        if (!screen.IntersectsWith(current) || inside.Contains(current))
        {
            return; // on another monitor, or already clear of the edges
        }

        _boundsBeforePattern = current;
        double width = Math.Max(_window.MinWidth, Math.Min(current.Width, inside.Width));
        double height = Math.Max(_window.MinHeight, Math.Min(current.Height, inside.Height));
        _window.Width = width;
        _window.Height = height;
        _window.Left = inside.X + (inside.Width - width) / 2;
        _window.Top = inside.Y + (inside.Height - height) / 2;
    }

    private MonitorInfo? SyncedMonitor()
    {
        try
        {
            List<MonitorInfo> monitors = MonitorEnumerator.List();
            string? name = _controller?.Settings.MonitorDeviceName;
            return monitors.FirstOrDefault(m => string.Equals(m.DeviceName, name, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(m => m.IsPrimary)
                ?? monitors.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn($"Test pattern: could not list monitors ({ex.Message})");
            return null;
        }
    }

    /// <summary>DXDark.ini was edited by hand while DX Dark runs: use the new settings everywhere.</summary>
    private void ApplyEditedSettings(AppSettings settings)
    {
        if (_controller is null || _vm is null || _exiting)
        {
            return;
        }

        _controller.ReplaceSettings(settings);
        _vm.ReloadAll();
        SyncStartWithWindows();
    }

    /// <summary>The Startup-folder shortcut follows StartWithWindows in DXDark.ini; leftovers in the registry are removed.</summary>
    private void SyncStartWithWindows()
    {
        if (_store is null || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        GeneralSettings general = _store.Current.General;
        bool enabled = StartupShortcut.Apply(general.StartWithWindows, exe);
        if (general.StartWithWindows != enabled)
        {
            general.StartWithWindows = enabled;
            _store.SaveSoon();
        }
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _testPattern?.Close();
        _testPattern = null;
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        _system?.Dispose();
        _tray?.Dispose();
        _vm?.Dispose();
        _controller?.Dispose();
        _store?.Dispose();
        _showEvent?.Dispose();
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unexpected error", e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// Developer aid: renders the main window in its main states, the calibration screens, a
    /// dialog and every effect and test pattern to PNG files using a synthetic test image, without
    /// touching the strip or capturing the screen.
    /// </summary>
    private void RenderSnapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        string settingsFolder = Path.Combine(folder, "settings");
        if (Directory.Exists(settingsFolder))
        {
            Directory.Delete(settingsFolder, recursive: true);
        }

        var store = new SettingsStore(settingsFolder);
        store.Load();
        store.Current.Layout = StripCatalog.DefaultLayout("000609", 32, 110, 16.0 / 9.0);
        store.Current.Strip = new StripMemory { ModelId = "000609", DisplaySizeInches = 32, LedCount = 110 };
        _controller = new LightController(store);
        _vm = new MainViewModel(_controller, Dispatcher, demoMode: true);

        var window = new MainWindow(_vm)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft - 3000,
            Top = SystemParameters.VirtualScreenTop,
        };
        window.Show();

        void Capture(string name)
        {
            _vm.RefreshState();
            Flush();
            _vm.PublishDemo();
            Flush();
            SavePng((FrameworkElement)window.Content, Path.Combine(folder, $"{name}.png"));
        }

        _vm.ShowDetails = true;
        Capture("main-screen");
        _vm.ShowDetails = false;
        _controller.SetEffect(BuiltInEffects.Aurora);
        Capture("main-effect");
        _controller.SetEffect(BuiltInEffects.Solid);
        Capture("main-solid");
        _controller.SetMode(LightMode.ScreenSync);
        window.ShowSettings();
        Capture("main-settings");
        window.HideSettings();

        // The Filter section, with a blur and a hue shift applied to the picture.
        _vm.Presets.FilterBlur = 0.03;
        _vm.Presets.FilterHueShift = 60;
        (window.FindName("PresetScroll") as ScrollViewer)?.ScrollToEnd();
        Capture("main-filter");
        _vm.Presets.FilterBlur = 0;
        _vm.Presets.FilterHueShift = 0;
        (window.FindName("PresetScroll") as ScrollViewer)?.ScrollToHome();

        // Calibration page, with the second zone selected (the red dot part-way along it).
        window.ShowPage(calibration: true);
        _vm.Calibration.SetArea(1, new ZoneArea(0.1, 0.03, 0.8, 0.22, 0.6)); // a hand-drawn area for the top
        _vm.Calibration.Select(_vm.Calibration.Zones[1]);
        Capture("main-calibration");

        // The test pattern behind the control panel, as on a 1440p screen.
        _vm.Calibration.SelectedPattern = _vm.Calibration.Patterns[1]; // colored arms; also switches the pattern on
        Capture("main-calibration-pattern");
        SavePng(PatternBehind(RenderBitmap((FrameworkElement)window.Content), CalibrationPattern.ColoredArms), Path.Combine(folder, "test-pattern.png"));
        _vm.Calibration.IsPatternShown = false;

        // First launch: no zones yet. Goes through the same path as a hand-edited settings file.
        AppSettings edited = SettingsIni.Read(SettingsIni.Write(store.Current, SettingsStore.Version))!;
        edited.Layout.Segments.Clear();
        _controller.ReplaceSettings(edited);
        _vm.ReloadAll();
        Capture("main-calibration-empty");
        window.ShowPage(calibration: false);
        Capture("main-no-zones");

        // The preset menu (a popup, so rendered on its own).
        if (window.FindName("PresetMenuButton") is Button { ContextMenu: { } menu } menuButton)
        {
            menu.PlacementTarget = menuButton;
            menu.IsOpen = true;
            Flush();
            SavePng(menu, Path.Combine(folder, "preset-menu.png"));
            menu.IsOpen = false;
        }

        window.AllowClose = true;
        window.Close();

        // A dialog over a window-sized backdrop.
        var dialog = new DialogWindow("Delete preset", "Delete the preset \"Gaming\"?", "Delete", "Cancel", destructive: true, input: null)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft - 3000,
            Top = SystemParameters.VirtualScreenTop,
            Width = 700,
            Height = 360,
        };
        dialog.Show();
        Flush();
        SavePng((FrameworkElement)dialog.Content, Path.Combine(folder, "dialog.png"));
        dialog.Close();

        var prompt = new DialogWindow("Save preset", "", "Save", "Cancel", destructive: false, input: "Cinema night")
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft - 3000,
            Top = SystemParameters.VirtualScreenTop,
            Width = 700,
            Height = 360,
        };
        prompt.Show();
        Flush();
        SavePng((FrameworkElement)prompt.Content, Path.Combine(folder, "dialog-prompt.png"));
        prompt.Close();

        // The file picker, on a folder of sample files.
        string samples = Path.Combine(folder, "picker");
        Directory.CreateDirectory(Path.Combine(samples, "Holiday clips"));
        foreach (string name in new[] { "Aurora loop.mp4", "Campfire.mov", "Neon city.mp4", "Rain on glass.mkv" })
        {
            File.WriteAllBytes(Path.Combine(samples, name), new byte[1_500_000]);
        }

        var picker = new FilePickerWindow("Add a video as an effect", "Add", "Videos: MP4, MOV, M4V, WMV, AVI, MKV",
            [".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv"], save: false, "snapshot", Environment.SpecialFolder.MyVideos, fileGlyph: "\uE8B2")
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft - 3000,
            Top = SystemParameters.VirtualScreenTop,
            Width = 1000,
            Height = 640,
        };
        picker.Show();
        Flush();
        picker.ShowFolder(samples);
        Flush();
        SavePng((FrameworkElement)picker.Content, Path.Combine(folder, "file-picker.png"));
        picker.Close();

        foreach (CalibrationPattern pattern in Enum.GetValues<CalibrationPattern>())
        {
            var canvas = new CalibrationCanvas { Width = 640, Height = 360, Pattern = pattern, FixedTime = 2.2 };
            canvas.Measure(new Size(640, 360));
            canvas.Arrange(new Rect(0, 0, 640, 360));
            canvas.UpdateLayout();
            SavePng(canvas, Path.Combine(folder, $"pattern-{pattern}.png"));
        }

        SavePng(EffectSheet(), Path.Combine(folder, "effects.png"));
        Shutdown();
    }

    /// <summary>A screen-sized test pattern with the control panel in the middle, as the user sees it.</summary>
    private static FrameworkElement PatternBehind(BitmapSource window, CalibrationPattern pattern)
    {
        const double W = 2560, H = 1440;
        var screen = new Grid { Width = W, Height = H };
        screen.Children.Add(new CalibrationCanvas { Pattern = pattern, FixedTime = 2.2 });
        screen.Children.Add(new Image
        {
            Source = window,
            Width = window.PixelWidth,
            Height = window.PixelHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        screen.Measure(new Size(W, H));
        screen.Arrange(new Rect(0, 0, W, H));
        screen.UpdateLayout();
        return screen;
    }

    /// <summary>Every built-in effect at its thumbnail moment, labelled, for checking how they look.</summary>
    private static FrameworkElement EffectSheet()
    {
        var sheet = new WrapPanel { Width = 1000, Background = Brushes.Black };
        foreach (BuiltInEffect effect in BuiltInEffects.All)
        {
            var frame = new DxDark.Capture.CapturedFrame();
            BuiltInEffects.Render(effect.Id, frame, BuiltInEffects.ThumbnailTime(effect.Id), 255, 122, 46, 192, 108);
            BitmapSource bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);
            var tile = new StackPanel { Margin = new Thickness(4) };
            tile.Children.Add(new Image { Source = bitmap, Width = 192, Height = 108 });
            tile.Children.Add(new TextBlock { Text = effect.Name, Foreground = Brushes.White, Margin = new Thickness(2, 4, 0, 6) });
            sheet.Children.Add(tile);
        }

        sheet.Measure(new Size(1000, double.PositiveInfinity));
        sheet.Arrange(new Rect(new Point(0, 0), new Size(1000, sheet.DesiredSize.Height)));
        sheet.UpdateLayout();
        return sheet;
    }

    private void Flush() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static BitmapSource RenderBitmap(FrameworkElement element)
    {
        int width = (int)Math.Ceiling(element.ActualWidth);
        int height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(FrameworkElement element, string path)
    {
        BitmapSource bitmap = RenderBitmap(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
