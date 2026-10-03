using System.Text.Json.Serialization;
using DxDark.Core.Effects;
using DxDark.Core.Layout;
using DxDark.Core.Profiles;

namespace DxDark.Core.Settings;

public enum LightMode
{
    /// <summary>The LEDs follow the screen.</summary>
    ScreenSync,

    /// <summary>The LEDs follow an effect canvas: a built-in animation, a solid color or a video.</summary>
    Effect,

    Off,

    /// <summary>The strip's own buttons took over; DX Dark sends nothing until a mode is picked.</summary>
    Controller,

    /// <summary>Version 0.0.1 only; read as <see cref="Effect"/> with the solid color.</summary>
    StaticColor,

    /// <summary>Version 0.0.1 only; read as <see cref="ScreenSync"/>.</summary>
    Rhythm,
}

public enum ExitAction
{
    TurnOff,
    KeepLastColors,

    /// <summary>Version 0.0.1 only; read as <see cref="TurnOff"/>.</summary>
    StartEffect,
}

/// <summary>Channel order of the strip, for strips wired differently from RGB.</summary>
public enum ColorOrder
{
    RGB,
    RBG,
    GRB,
    GBR,
    BRG,
    BGR,
}

/// <summary>Properties of the physical strip rather than of the picture.</summary>
public sealed class CalibrationSettings
{
    /// <summary>White balance of the LEDs themselves (1 = unchanged).</summary>
    public double RedGain { get; set; } = 1;

    public double GreenGain { get; set; } = 1;

    public double BlueGain { get; set; } = 1;

    public ColorOrder ColorOrder { get; set; } = ColorOrder.RGB;

    /// <summary>
    /// Share of the strip's full-white power that may be drawn at once. 1/3 equals DX Light's limit
    /// (no LED above R+G+B = 255), which keeps the USB-powered strip within what a port supplies.
    /// </summary>
    public double PowerLimit { get; set; } = 1.0 / 3.0;

    /// <summary>The strip's own brightness, 5–255, applied by the controller on top of the preset's brightness.</summary>
    public int HardwareBrightness { get; set; } = 255;

    public CalibrationSettings Clone() => (CalibrationSettings)MemberwiseClone();

    public void Normalize()
    {
        RedGain = Math.Clamp(RedGain, 0, 1);
        GreenGain = Math.Clamp(GreenGain, 0, 1);
        BlueGain = Math.Clamp(BlueGain, 0, 1);
        PowerLimit = Math.Clamp(PowerLimit, 0.1, 1);
        HardwareBrightness = Math.Clamp(HardwareBrightness, 5, 255);
        if (!Enum.IsDefined(ColorOrder))
        {
            ColorOrder = ColorOrder.RGB;
        }
    }
}

/// <summary>A video the user added as an effect; the file is copied into DX Dark's video folder.</summary>
public sealed class VideoEffect
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Video";

    /// <summary>File name inside the video folder.</summary>
    public string FileName { get; set; } = "";
}

public sealed class LightingSettings
{
    public LightMode Mode { get; set; } = LightMode.ScreenSync;

    /// <summary>Selected effect: a built-in effect ID or a <see cref="VideoEffect.Id"/>.</summary>
    public string EffectId { get; set; } = BuiltInEffects.Rainbow;

    /// <summary>Playback speed of effects (1 = normal).</summary>
    public double EffectSpeed { get; set; } = 1.0;

    /// <summary>Color of the Solid and Pulse effects.</summary>
    [JsonPropertyName("StaticColor")]
    public string SolidColor { get; set; } = "#FF7A2E";

    public List<VideoEffect> Videos { get; set; } = [];

    public void Normalize()
    {
        Videos ??= [];
        Videos.RemoveAll(v => v is null || string.IsNullOrWhiteSpace(v.Id) || string.IsNullOrWhiteSpace(v.FileName));

        switch (Mode)
        {
            case LightMode.StaticColor:
                Mode = LightMode.Effect;
                EffectId = BuiltInEffects.Solid;
                break;
            case LightMode.Rhythm:
            case var unknown when !Enum.IsDefined(unknown):
                Mode = LightMode.ScreenSync;
                break;
        }

        if (BuiltInEffects.Find(EffectId) is null && !Videos.Any(v => v.Id == EffectId))
        {
            EffectId = BuiltInEffects.Rainbow;
        }

        // Version 0.0.1 stored the speed as 0–100.
        EffectSpeed = EffectSpeed is > 0 and <= 4 ? Math.Clamp(EffectSpeed, 0.25, 3) : 1.0;

        if (!Color.ColorMath.TryParseHex(SolidColor, out _, out _, out _))
        {
            SolidColor = "#FF7A2E";
        }
    }
}

public sealed class GeneralSettings
{
    public ExitAction OnExit { get; set; } = ExitAction.TurnOff;

    public bool OffWhenLocked { get; set; } = true;

    public bool OffWhenDisplayOff { get; set; } = true;

    /// <summary>Warn when DX Light is running at the same time.</summary>
    public bool WarnAboutDxLight { get; set; } = true;

    /// <summary>
    /// Start DX Dark (in the tray) when Windows starts, through a shortcut in the Startup folder that
    /// is kept in step with this setting. Null when not known yet (settings from before 0.1.1).
    /// </summary>
    public bool? StartWithWindows { get; set; }

    public void Normalize()
    {
        if (OnExit is not (ExitAction.TurnOff or ExitAction.KeepLastColors))
        {
            OnExit = ExitAction.TurnOff;
        }
    }
}

/// <summary>The strip DX Dark last talked to, so zones can be edited while it is unplugged.</summary>
public sealed class StripMemory
{
    public string? ModelId { get; set; }

    public int DisplaySizeInches { get; set; }

    public int LedCount { get; set; }

    public void Normalize()
    {
        ModelId = string.IsNullOrWhiteSpace(ModelId) ? null : ModelId.Trim();
        DisplaySizeInches = Math.Clamp(DisplaySizeInches, 0, 100);
        LedCount = Math.Clamp(LedCount, 0, 254);
    }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 3;

    /// <summary>The zones, set up by the user in Calibration.</summary>
    public LedLayout Layout { get; set; } = new();

    public CalibrationSettings Calibration { get; set; } = new();

    public StripMemory Strip { get; set; } = new();

    /// <summary>Global keyboard shortcuts, one per <see cref="ShortcutAction"/>.</summary>
    public List<Shortcut> Shortcuts { get; set; } = ShortcutDefaults.Complete(null);

    public LightingSettings Lighting { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    public List<Profile> Profiles { get; set; } = BuiltInProfiles.CreateAll();

    public string ActiveProfile { get; set; } = BuiltInProfiles.DefaultName;

    /// <summary>Monitor to sample (e.g. \\.\DISPLAY1); null means the primary monitor.</summary>
    public string? MonitorDeviceName { get; set; }

    public Profile GetActiveProfile() =>
        Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles[0];

    public void Normalize()
    {
        Layout ??= new LedLayout();
        Layout.Segments ??= [];
        Layout.Segments.RemoveAll(s => s is null);
        foreach (LedSegment segment in Layout.Segments)
        {
            segment.LedCount = Math.Clamp(segment.LedCount, 1, 254);
            segment.Nudge = double.IsFinite(segment.Nudge) ? Math.Clamp(segment.Nudge, -20, 20) : 0;
            segment.SampleDepth = segment.SampleDepth is { } depth && double.IsFinite(depth) ? Math.Clamp(depth, 0.01, 0.5) : null;
            segment.ZoneOverlap = segment.ZoneOverlap is { } overlap && double.IsFinite(overlap) ? Math.Clamp(overlap, 0, 3) : null;
            if (!Enum.IsDefined(segment.Edge) || segment.Edge == ScreenEdge.None)
            {
                segment.Edge = ScreenEdge.Top;
            }
        }

        // Older zones keep their edges, LED counts, orientation and nudges; the 0.0.1 per-zone
        // coverage and global offset are dropped (zones span whole edges and are adjusted with Nudge).
        SchemaVersion = 3;

        Calibration ??= new CalibrationSettings();
        Calibration.Normalize();
        Strip ??= new StripMemory();
        Strip.Normalize();
        Lighting ??= new LightingSettings();
        Lighting.Normalize();
        General ??= new GeneralSettings();
        General.Normalize();
        Shortcuts = ShortcutDefaults.Complete(Shortcuts);

        Profiles ??= [];
        Profiles.RemoveAll(p => p is null);
        if (Profiles.Count == 0)
        {
            Profiles = BuiltInProfiles.CreateAll();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Profile profile in Profiles)
        {
            profile.Normalize();
            string baseName = profile.Name;
            for (int i = 2; !seen.Add(profile.Name); i++)
            {
                profile.Name = $"{baseName} ({i})";
            }
        }

        if (!Profiles.Any(p => p.Name == ActiveProfile))
        {
            ActiveProfile = Profiles[0].Name;
        }

        MoveOldZoneSampling();
        foreach (Profile profile in Profiles)
        {
            profile.TrimAreas(Layout.Segments.Count);
        }
    }

    /// <summary>
    /// Versions 0.0.2–0.2.0 kept a nudge (and in 0.2.0 a depth and overlap) on each zone. Zones
    /// now sample an area saved in each preset, so those become an area in every preset that has
    /// none for the zone yet: the usual area, shifted by the nudge.
    /// </summary>
    private void MoveOldZoneSampling()
    {
        for (int s = 0; s < Layout.Segments.Count; s++)
        {
            LedSegment zone = Layout.Segments[s];
            if (!zone.HasOldSampling)
            {
                continue;
            }

            foreach (Profile profile in Profiles)
            {
                if (profile.AreaAt(s) is not null)
                {
                    continue;
                }

                ZoneArea area = ZoneArea.Default(zone.Edge, zone.SampleDepth ?? profile.SampleDepth, zone.ZoneOverlap ?? profile.ZoneOverlap, 16.0 / 9.0);
                double shift = zone.LedCount > 0 ? zone.Nudge / zone.LedCount : 0;
                area = LedSegment.IsHorizontal(zone.Edge) ? area with { X = area.X + shift * area.Width } : area with { Y = area.Y + shift * area.Height };
                profile.SetArea(s, area);
            }

            zone.Nudge = 0;
            zone.SampleDepth = null;
            zone.ZoneOverlap = null;
        }
    }
}
