using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DxDark.App.Controls;
using DxDark.App.Infrastructure;
using DxDark.Core;
using DxDark.Core.Layout;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;
using DxDark.Protocol;

namespace DxDark.App.ViewModels;

public sealed record PatternChoice(CalibrationPattern Pattern, string Name);

public sealed record EdgeChoice(ScreenEdge Edge, string Name);

/// <summary>What an edit to a zone should show on the strip.</summary>
internal enum ZoneEdit
{
    /// <summary>Nothing (e.g. a nudge, which shows in the picture instead).</summary>
    Quiet,

    /// <summary>Run the red dot along the zone again.</summary>
    Replay,

    /// <summary>The LED count is being dragged: show the zone's extent, and the dot once it settles.</summary>
    Count,
}

/// <summary>One zone in the calibration panel.</summary>
public sealed class ZoneViewModel : ObservableObject
{
    private readonly CalibrationViewModel _owner;
    private bool _isSelected;

    public ZoneViewModel(CalibrationViewModel owner, LedSegment model)
    {
        _owner = owner;
        Model = model;
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        ResetAreaCommand = new RelayCommand(() => _owner.ResetArea(Index));
    }

    public LedSegment Model { get; }

    public int Number { get; private set; }

    public ICommand RemoveCommand { get; }

    public IReadOnlyList<EdgeChoice> EdgeChoices => CalibrationViewModel.Edges;

    public ScreenEdge Edge
    {
        get => Model.Edge;
        set
        {
            if (Model.Edge != value)
            {
                // Keep the direction around the screen (clockwise or not) when the edge changes.
                bool clockwise = Model.Reversed == LedSegment.ClockwiseReversed(Model.Edge);
                Model.Edge = value;
                Model.Reversed = clockwise ? LedSegment.ClockwiseReversed(value) : !LedSegment.ClockwiseReversed(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(OrientationChoices));
                OnPropertyChanged(nameof(OrientationIndex));
                OnPropertyChanged(nameof(DetectedLeds));
                _owner.Edited(this, ZoneEdit.Replay);
            }
        }
    }

    public string[] OrientationChoices => LedSegment.IsHorizontal(Model.Edge)
        ? ["→  Left to right", "←  Right to left"]
        : ["↓  Top to bottom", "↑  Bottom to top"];

    /// <summary>0 = left→right / top→bottom, 1 = the reverse.</summary>
    public int OrientationIndex
    {
        get => Model.Reversed ? 1 : 0;
        set
        {
            if (value >= 0 && Model.Reversed != (value == 1))
            {
                Model.Reversed = value == 1;
                OnPropertyChanged();
                _owner.Edited(this, ZoneEdit.Replay);
            }
        }
    }

    public double LedCount
    {
        get => Model.LedCount;
        set
        {
            int count = (int)Math.Clamp(Math.Round(value), 1, LedRange.MaxLed);
            if (count != Model.LedCount)
            {
                Model.LedCount = count;
                OnPropertyChanged();
                _owner.Edited(this, ZoneEdit.Count);
            }
        }
    }

    /// <summary>The slider goes up to the strip's whole LED count.</summary>
    public double MaxLeds => _owner.MaxLeds;

    /// <summary>The detected count for this zone's edge; the slider's reset button goes back to it.</summary>
    public double DetectedLeds => _owner.DetectedFor(Model.Edge);

    /// <summary>Overlap of this zone's area in the current preset.</summary>
    public double ZoneOverlap
    {
        get => _owner.AreaOf(Index).Overlap;
        set
        {
            ZoneArea area = _owner.AreaOf(Index);
            if (Math.Abs(area.Overlap - value) > 0.0005)
            {
                _owner.SetArea(Index, area with { Overlap = value });
            }
        }
    }

    /// <summary>The preset's usual overlap; the reset button goes back to it.</summary>
    public double PresetOverlap => _owner.Preset.ZoneOverlap;

    /// <summary>Puts this zone's area in the current preset back to the whole edge.</summary>
    public ICommand ResetAreaCommand { get; }

    private int Index => _owner.Zones.IndexOf(this);

    internal void RefreshArea()
    {
        OnPropertyChanged(nameof(ZoneOverlap));
        OnPropertyChanged(nameof(PresetOverlap));
    }

    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }

    internal void Refresh(int number)
    {
        Number = number;
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(MaxLeds));
        OnPropertyChanged(nameof(DetectedLeds));
        OnPropertyChanged(nameof(LedCount));
        RefreshArea();
    }
}

/// <summary>
/// Calibration: the zones (added one by one, each shown on the strip with a running red dot), the
/// LED color and the full-screen test patterns.
/// </summary>
public sealed class CalibrationViewModel : ObservableObject
{
    public static readonly IReadOnlyList<EdgeChoice> Edges =
    [
        new(ScreenEdge.Left, "Left"),
        new(ScreenEdge.Top, "Top"),
        new(ScreenEdge.Right, "Right"),
        new(ScreenEdge.Bottom, "Bottom"),
    ];

    private const int MaxZones = 12;

    private readonly LightController _controller;
    private readonly bool _demoMode;
    private PatternChoice _pattern;
    private double _patternSpeed = 1;
    private ZoneViewModel? _selectedZone;
    private bool _patternShown;
    private (int Horizontal, int Vertical) _sides;

    public CalibrationViewModel(LightController controller, bool demoMode = false)
    {
        _controller = controller;
        _demoMode = demoMode;
        Patterns =
        [
            new(CalibrationPattern.SpinningCross, "Cross"),
            new(CalibrationPattern.ColoredArms, "Colored arms"),
            new(CalibrationPattern.EdgeColors, "Edges"),
            new(CalibrationPattern.BorderChase, "Chase"),
            new(CalibrationPattern.ColorCycle, "Cycle"),
            new(CalibrationPattern.White, "White"),
            new(CalibrationPattern.RainbowRing, "Rainbow ring"),
        ];
        _pattern = Patterns[0];
        Zones = [];
        _sides = controller.EdgeLedCounts();
        Reload();

        AddZoneCommand = new RelayCommand(AddZone, () => Zones.Count < MaxZones);
        ClearCommand = new RelayCommand(Clear, () => Zones.Count > 0);
        ColorOrders = Enum.GetValues<ColorOrder>();
    }

    /// <summary>Raised when the pattern or its speed changes.</summary>
    public event Action? PatternChanged;

    /// <summary>Raised when another zone is selected (or none).</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when a zone's area changes, or the preset (and with it every area).</summary>
    public event Action? AreasChanged;

    public IReadOnlyList<PatternChoice> Patterns { get; }

    public ObservableCollection<ZoneViewModel> Zones { get; }

    public IReadOnlyList<ColorOrder> ColorOrders { get; }

    public ICommand AddZoneCommand { get; }

    public ICommand ClearCommand { get; }

    private LedLayout Layout => _controller.Settings.Layout;

    private CalibrationSettings Calibration => _controller.Settings.Calibration;

    public PatternChoice SelectedPattern
    {
        get => _pattern;
        set
        {
            if (value is not null && Set(ref _pattern, value))
            {
                PatternChanged?.Invoke();
                IsPatternShown = true; // picking a pattern shows it
            }
        }
    }

    /// <summary>The test pattern fills the synced screen behind the control panel.</summary>
    public bool IsPatternShown { get => _patternShown; set => Set(ref _patternShown, value); }

    internal Profile Preset => _controller.Settings.GetActiveProfile();

    /// <summary>The area every zone samples in the current preset, for a picture of the given width / height.</summary>
    public IReadOnlyList<ZoneArea> AreasFor(double aspect) => LayoutGeometry.Areas(Layout, Preset, aspect);

    internal ZoneArea AreaOf(int zone) =>
        zone >= 0 && zone < Layout.Segments.Count ? AreasFor(_controller.ScreenAspect)[zone] : default;

    /// <summary>Sets zone <paramref name="zone"/>'s area in the current preset (dragged in the live view).</summary>
    public void SetArea(int zone, ZoneArea area)
    {
        if (zone < 0 || zone >= Zones.Count)
        {
            return;
        }

        Preset.SetArea(zone, area);
        _controller.ProfileChanged();
        Zones[zone].RefreshArea();
        AreasChanged?.Invoke();
    }

    public void ResetArea(int zone)
    {
        if (zone < 0 || zone >= Zones.Count)
        {
            return;
        }

        Preset.SetArea(zone, null);
        _controller.ProfileChanged();
        Zones[zone].RefreshArea();
        AreasChanged?.Invoke();
    }

    /// <summary>Moves the selected zone's area by a fraction of the picture (arrow keys).</summary>
    public void MoveSelectedArea(double dx, double dy)
    {
        if (SelectedIndex is int zone and >= 0)
        {
            ZoneArea area = AreaOf(zone);
            SetArea(zone, area with { X = area.X + dx, Y = area.Y + dy });
        }
    }

    public double PatternSpeed
    {
        get => _patternSpeed;
        set
        {
            if (Set(ref _patternSpeed, value))
            {
                PatternChanged?.Invoke();
            }
        }
    }

    public ZoneViewModel? SelectedZone
    {
        get => _selectedZone;
        private set
        {
            if (_selectedZone == value)
            {
                return;
            }

            if (_selectedZone is not null)
            {
                _selectedZone.IsSelected = false;
            }

            _selectedZone = value;
            if (value is not null)
            {
                value.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedIndex));
            SelectionChanged?.Invoke();
        }
    }

    public int SelectedIndex => _selectedZone is null ? -1 : Zones.IndexOf(_selectedZone);

    public bool HasZones => Zones.Count > 0;

    public double MaxLeds => _controller.StripLedCount;

    /// <summary>How many LEDs the strip has and how many belong on each edge, as a guide for the LED counts.</summary>
    public string DetectedText
    {
        get
        {
            string counts = $"Top: {_sides.Horizontal}, Bottom: {_sides.Horizontal}, Left: {_sides.Vertical}, Right: {_sides.Vertical}";
            int leds = _controller.StripLedCount;
            if (_demoMode || _controller.IsConnected)
            {
                return $"Detected {leds} LEDs.\n{counts}";
            }

            return _controller.IsStripKnown ? $"Not connected. Last seen: {leds} LEDs.\n{counts}" : "Connect the strip to detect its LEDs.";
        }
    }

    public string LedSummary
    {
        get
        {
            int used = Layout.LedCount, strip = _controller.StripLedCount;
            string summary = $"{used} of {strip} LEDs";
            return !HasZones ? summary
                : used < strip ? $"{summary} · {strip - used} not in a zone"
                : used > strip ? $"{summary} · {used - strip} too many"
                : summary;
        }
    }

    public bool SummaryIsWarning => HasZones && Layout.LedCount != _controller.StripLedCount;

    public double RedGain { get => Calibration.RedGain; set => EditCalibration(c => c.RedGain = value); }

    public double GreenGain { get => Calibration.GreenGain; set => EditCalibration(c => c.GreenGain = value); }

    public double BlueGain { get => Calibration.BlueGain; set => EditCalibration(c => c.BlueGain = value); }

    public ColorOrder ColorOrder { get => Calibration.ColorOrder; set => EditCalibration(c => c.ColorOrder = value); }

    /// <summary>The strip's own brightness limit, 2–100 %.</summary>
    public double StripBrightness
    {
        get => Calibration.HardwareBrightness / 255.0;
        set
        {
            _controller.SetStripBrightness((int)Math.Round(value * 255));
            OnPropertyChanged();
        }
    }

    /// <summary>Rebuilds the zone list from settings (after the settings file was reloaded).</summary>
    public void Reload()
    {
        SelectedZone = null;
        Zones.Clear();
        foreach (LedSegment segment in Layout.Segments)
        {
            Zones.Add(new ZoneViewModel(this, segment));
        }

        Renumber();
        OnAllPropertiesChanged();
    }

    /// <summary>The strip was connected or disconnected: refresh the LED guide and limits.</summary>
    public void Refresh()
    {
        _sides = _controller.EdgeLedCounts();
        Renumber();
        OnPropertyChanged(nameof(MaxLeds));
        OnPropertyChanged(nameof(DetectedText));
        OnPropertyChanged(nameof(StripBrightness));
        AreasChanged?.Invoke();
    }

    /// <summary>Deselects the zone and stops showing it on the strip (leaving Calibration, hiding the window).</summary>
    public void ClearSelection()
    {
        SelectedZone = null;
        _controller.StopShowingZone();
    }

    /// <summary>Selects a zone and shows it on the strip, the red dot running along it until another zone is picked.</summary>
    public void Select(ZoneViewModel zone)
    {
        int index = Zones.IndexOf(zone);
        if (index < 0)
        {
            return;
        }

        SelectedZone = zone;
        _controller.ShowZone(index);
    }

    internal int DetectedFor(ScreenEdge edge) => LedSegment.IsHorizontal(edge) ? _sides.Horizontal : _sides.Vertical;

    internal void Edited(ZoneViewModel zone, ZoneEdit edit)
    {
        _controller.ZonesChanged();
        Renumber();
        switch (edit)
        {
            case ZoneEdit.Replay:
                Select(zone);
                break;
            case ZoneEdit.Count:
                SelectedZone = zone;
                _controller.ShowZone(Zones.IndexOf(zone), dotDelaySeconds: 0.6);
                break;
        }
    }

    internal void Remove(ZoneViewModel zone)
    {
        if (SelectedZone == zone)
        {
            SelectedZone = null;
        }

        _controller.StopShowingZone();
        int index = Zones.IndexOf(zone);
        foreach (Profile profile in _controller.Settings.Profiles)
        {
            profile.RemoveZone(index);
        }

        Layout.Segments.Remove(zone.Model);
        Zones.Remove(zone);
        _controller.ProfileChanged();
        AreasChanged?.Invoke();
        _controller.ZonesChanged();
        Renumber();
        if (SelectedZone is { } still)
        {
            Select(still); // its LEDs may have moved
        }
    }

    private void AddZone()
    {
        (ScreenEdge edge, bool reversed) = LedSegment.Following(Layout.Segments.LastOrDefault());
        int remaining = _controller.StripLedCount - Layout.LedCount;
        int detected = DetectedFor(edge);
        var segment = new LedSegment
        {
            Edge = edge,
            Reversed = reversed,
            LedCount = Math.Max(1, remaining > 0 ? Math.Min(detected, remaining) : detected),
        };
        Layout.Segments.Add(segment);
        var zone = new ZoneViewModel(this, segment);
        Zones.Add(zone);
        _controller.ZonesChanged();
        Renumber();
        AreasChanged?.Invoke();
        Select(zone);
    }

    private void Clear()
    {
        if (!Dialogs.Confirm("Remove all zones", "Remove every zone? The strip stays dark until you add zones again.", "Remove all", destructive: true))
        {
            return;
        }

        SelectedZone = null;
        _controller.StopShowingZone();
        foreach (Profile profile in _controller.Settings.Profiles)
        {
            profile.TrimAreas(0);
        }

        Layout.Segments.Clear();
        Zones.Clear();
        _controller.ProfileChanged();
        AreasChanged?.Invoke();
        _controller.ZonesChanged();
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Zones.Count; i++)
        {
            Zones[i].Refresh(i + 1);
        }

        OnPropertyChanged(nameof(HasZones));
        OnPropertyChanged(nameof(LedSummary));
        OnPropertyChanged(nameof(SummaryIsWarning));
        OnPropertyChanged(nameof(SelectedIndex));
        CommandManager.InvalidateRequerySuggested();
    }

    private void EditCalibration(Action<CalibrationSettings> change, [CallerMemberName] string? property = null)
    {
        change(Calibration);
        Calibration.Normalize();
        _controller.CalibrationChanged();
        OnPropertyChanged(property);
    }
}
