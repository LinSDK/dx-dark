using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DxDark.App.Infrastructure;
using DxDark.Core;
using DxDark.Core.Effects;
using DxDark.Core.Layout;
using DxDark.Core.Settings;
using DxDark.Core.Sync;
using DxDark.Protocol;

namespace DxDark.App.ViewModels;

/// <summary>The main window: source switch, live view routing, status card and its details.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _statsTimer;
    private readonly HashSet<object> _previewRequesters = [];
    private DispatcherTimer? _demoTimer;
    private int _previewPending;
    private string _statsText = "";
    private bool _showDetails;
    private bool _isCalibrationPage;
    private bool _isShortcutsPage;

    public MainViewModel(LightController controller, Dispatcher ui, bool demoMode)
    {
        Controller = controller;
        _ui = ui;
        DemoMode = demoMode;
        Presets = new PresetViewModel(controller);
        Effects = new EffectsViewModel(controller, ui);
        Settings = new SettingsViewModel(controller, demoMode);
        Calibration = new CalibrationViewModel(controller, demoMode);
        Shortcuts = new ShortcutsViewModel(controller);
        Calibration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CalibrationViewModel.HasZones) or nameof(CalibrationViewModel.LedSummary) or "")
            {
                OnPropertyChanged(nameof(HasNoZones));
                OnPropertyChanged(nameof(ShowNoZonesWarning));
                OnPropertyChanged(nameof(StatusDetail));
            }
        };

        controller.StateChanged += () => _ui.BeginInvoke(RefreshState);
        controller.LayoutReplaced += () => _ui.BeginInvoke(() => LayoutReplaced?.Invoke());
        controller.Engine.PreviewReady += OnPreviewFromEngine;

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats(), ui);
        _statsTimer.Start();
    }

    public LightController Controller { get; }

    /// <summary>UI snapshots only: shows a test image instead of capturing the screen.</summary>
    public bool DemoMode { get; }

    public PresetViewModel Presets { get; }

    public EffectsViewModel Effects { get; }

    public SettingsViewModel Settings { get; }

    public CalibrationViewModel Calibration { get; }

    public ShortcutsViewModel Shortcuts { get; }

    /// <summary>Raised on the UI thread with a snapshot that is only valid during the call.</summary>
    public event Action<PreviewSnapshot>? PreviewReceived;

    /// <summary>All settings were replaced (the settings file was edited by hand).</summary>
    public event Action? LayoutReplaced;

    public string VersionText => $"Version {Settings.Version}";

    // ── Pages ───────────────────────────────────────────────────────────────

    /// <summary>The control panel shows Calibration (zones and LED color) instead of the preset strip.</summary>
    public bool IsCalibrationPage
    {
        get => _isCalibrationPage;
        set
        {
            if (Set(ref _isCalibrationPage, value))
            {
                if (value)
                {
                    IsShortcutsPage = false;
                }

                OnPropertyChanged(nameof(IsLivePage));
                OnPropertyChanged(nameof(ShowEffectGallery));
                OnPropertyChanged(nameof(ShowNoZonesWarning));
                if (!value)
                {
                    Calibration.ClearSelection();
                    Calibration.IsPatternShown = false;
                }
            }
        }
    }

    /// <summary>The control panel shows the keyboard shortcuts instead of the preset strip.</summary>
    public bool IsShortcutsPage
    {
        get => _isShortcutsPage;
        set
        {
            if (Set(ref _isShortcutsPage, value))
            {
                if (value)
                {
                    IsCalibrationPage = false;
                }

                OnPropertyChanged(nameof(IsLivePage));
            }
        }
    }

    public bool IsLivePage => !_isCalibrationPage && !_isShortcutsPage;

    public bool ShowEffectGallery => IsEffect;

    public bool HasNoZones => !Calibration.HasZones;

    /// <summary>A small warning on the live view after leaving Calibration without adding zones.</summary>
    public bool ShowNoZonesWarning => HasNoZones && !_isCalibrationPage;

    // ── Source ──────────────────────────────────────────────────────────────

    private LightMode Mode => Controller.Settings.Lighting.Mode;

    public bool IsScreen { get => Mode == LightMode.ScreenSync; set => SetModeIf(value, LightMode.ScreenSync); }

    public bool IsEffect { get => Mode == LightMode.Effect; set => SetModeIf(value, LightMode.Effect); }

    public bool IsOff { get => Mode == LightMode.Off; set => SetModeIf(value, LightMode.Off); }

    public bool IsControllerMode => Mode == LightMode.Controller;

    public string ModeName => Mode switch
    {
        LightMode.ScreenSync => "Screen",
        LightMode.Effect => Effects.SelectedTile?.Name ?? "Effect",
        LightMode.Off => "Off",
        _ => "Strip buttons",
    };

    // ── Status card ─────────────────────────────────────────────────────────

    public string StatusTitle =>
        DemoMode ? "Connected"
        : !Controller.IsConnected ? "Not connected"
        : Controller.IsSuspended ? "Paused"
        : "Connected";

    public string StatusDetail =>
        !DemoMode && !Controller.IsConnected ? Controller.ConnectionProblem ?? "Plug in the strip"
        : !DemoMode && Controller.IsSuspended ? "PC locked, asleep or display off"
        : $"{(DemoMode ? 110 : Controller.StripInfo?.LedCount)} LEDs · {ZoneCountText}";

    private string ZoneCountText => Calibration.Zones.Count == 1 ? "1 zone" : $"{Calibration.Zones.Count} zones";

    public Brush StatusBrush => (Brush)Application.Current.Resources[
        DemoMode || (Controller.IsConnected && !Controller.IsSuspended) ? "SuccessBrush"
        : Controller.IsConnected ? "WarningBrush"
        : Controller.ConnectionProblem is not null ? "DangerBrush"
        : "IdleBrush"];

    public bool ShowDetails { get => _showDetails; set => Set(ref _showDetails, value); }

    public string DetailModel => DemoMode ? "000609 · DX Light backlight"
        : Controller.StripInfo is { } i ? $"{i.ModelId}{(StripCatalog.IsKnownModel(i.ModelId) ? " · DX Light backlight" : "")}" : "–";

    public string DetailFirmware => DemoMode ? "1.8.2" : Controller.StripInfo?.Firmware.ToString() ?? "–";

    public string DetailLeds => DemoMode ? "110" : Controller.StripInfo?.LedCount.ToString() ?? "–";

    public string DetailKit => DemoMode ? "32\"" : Controller.StripInfo is { } i ? $"{i.DisplaySizeInches}\"" : "–";

    public string DetailId => DemoMode ? "0123456789abcdef" : Controller.StripInfo?.Uuid ?? "–";

    public string DetailZones => $"{LightStrip.ZoneBoundaries(DemoMode ? 110 : Controller.StripLedCount).Length - 1} per frame";

    public string StatsText { get => _statsText; private set => Set(ref _statsText, value); }

    /// <summary>The live view asks for previews while visible; the engine runs only while someone asks.</summary>
    public void RequestPreview(object requester, bool wanted)
    {
        bool changed = wanted ? _previewRequesters.Add(requester) : _previewRequesters.Remove(requester);
        if (!changed)
        {
            return;
        }

        bool any = _previewRequesters.Count > 0;
        if (DemoMode)
        {
            if (any && _demoTimer is null)
            {
                _demoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => PublishDemo(), _ui);
                _demoTimer.Start();
                PublishDemo();
            }
            else if (!any)
            {
                _demoTimer?.Stop();
                _demoTimer = null;
            }

            return;
        }

        Controller.SetPreviewEnabled(any);
    }

    public void PublishDemo() =>
        PreviewReceived?.Invoke(DemoPreview.Create(Controller.Settings, _isCalibrationPage ? Calibration.SelectedIndex : -1));

    /// <summary>Re-reads everything after the settings were replaced (the settings file was edited).</summary>
    public void ReloadAll()
    {
        Effects.ReloadVideos();
        Calibration.Reload();
        Shortcuts.Reload();
        Presets.RefreshMonitors();
        RefreshState();
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(IsScreen));
        OnPropertyChanged(nameof(IsEffect));
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(IsControllerMode));
        OnPropertyChanged(nameof(ShowEffectGallery));
        OnPropertyChanged(nameof(HasNoZones));
        OnPropertyChanged(nameof(ShowNoZonesWarning));
        OnPropertyChanged(nameof(ModeName));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(DetailModel));
        OnPropertyChanged(nameof(DetailFirmware));
        OnPropertyChanged(nameof(DetailLeds));
        OnPropertyChanged(nameof(DetailKit));
        OnPropertyChanged(nameof(DetailId));
        OnPropertyChanged(nameof(DetailZones));
        Effects.Refresh();
        Presets.Refresh();
        Settings.Refresh();
        Calibration.Refresh();
        UpdateStats();
    }

    private void SetModeIf(bool selected, LightMode mode)
    {
        if (selected && Mode != mode)
        {
            Controller.SetMode(mode);
            RefreshState();
        }
    }

    private void OnPreviewFromEngine(PreviewSnapshot snapshot)
    {
        // Engine thread. Drop the frame if the UI has not drawn the previous one yet.
        if (Interlocked.Exchange(ref _previewPending, 1) == 1)
        {
            Controller.Engine.Recycle(snapshot);
            return;
        }

        _ui.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                PreviewReceived?.Invoke(snapshot);
            }
            finally
            {
                Controller.Engine.Recycle(snapshot);
                Volatile.Write(ref _previewPending, 0);
            }
        });
    }

    private void UpdateStats()
    {
        if (DemoMode)
        {
            StatsText = "144 Hz · 60 fps";
            return;
        }

        SyncStats s = Controller.Engine.Stats;
        if (s.Error is { } error)
        {
            StatsText = error;
        }
        else if (s.CaptureFps <= 0 && s.SendFps <= 0)
        {
            StatsText = "";
        }
        else
        {
            var parts = new List<string>();
            if (s.RefreshRate > 0)
            {
                parts.Add($"{s.RefreshRate:0} Hz");
            }

            parts.Add(s.Sending ? $"{s.SendFps:0} fps" : "not sending");
            if (s.Sending && s.PowerScale < 0.99)
            {
                parts.Add($"power limit {s.PowerScale:P0}");
            }

            StatsText = string.Join(" · ", parts);
        }
    }

    public void Dispose()
    {
        _statsTimer.Stop();
        _demoTimer?.Stop();
        Controller.Engine.PreviewReady -= OnPreviewFromEngine;
    }
}
