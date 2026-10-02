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
    private CalibrationWindow? _testPattern;
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
            _window = new MainWindow(_vm, ShowTestPattern);
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

    /// <summary>The full-screen test pattern, with the zone controls over it.</summary>
    private void ShowTestPattern()
    {
        if (_controller is null || _vm is null || _exiting)
        {
            return;
        }

        if (_testPattern is { IsVisible: true })
        {
            _testPattern.Activate();
            return;
        }

        _testPattern = new CalibrationWindow(_controller, _vm.Calibration);
        _testPattern.Closed += (_, _) =>
        {
            _testPattern = null;
            _vm?.RefreshState();
        };
        _testPattern.Show();
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

        var window = new MainWindow(_vm, () => { })
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

        // Calibration page, with the second zone selected (the red dot part-way along it).
        window.ShowPage(calibration: true);
        _vm.Calibration.Select(_vm.Calibration.Zones[1]);
        Capture("main-calibration");

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

        var prompt = new DialogWindow("Save as new preset", "", "Save", "Cancel", destructive: false, input: "My preset")
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

        // Full-screen test pattern with the zone controls.
        store.Current.Layout = StripCatalog.DefaultLayout("000609", 32, 110, 16.0 / 9.0);
        _vm.Calibration.Reload();
        var calibration = new CalibrationWindow(_controller, _vm.Calibration, live: false)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = false,
            Width = 1600,
            Height = 900,
            Left = SystemParameters.VirtualScreenLeft - 4000,
            Top = SystemParameters.VirtualScreenTop,
        };
        calibration.FreezeAt(2.2);
        calibration.Show();
        Flush();
        SavePng((FrameworkElement)calibration.Content, Path.Combine(folder, "test-pattern.png"));
        calibration.Close();

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

    private static void SavePng(FrameworkElement element, string path)
    {
        int width = (int)Math.Ceiling(element.ActualWidth);
        int height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
