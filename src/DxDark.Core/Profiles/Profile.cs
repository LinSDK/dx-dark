using DxDark.Core.Layout;

namespace DxDark.Core.Profiles;

/// <summary>
/// A saved set of screen-sync picture settings ("preset"). Everything about how the screen is
/// sampled and how colors are shaped lives here; the physical layout and strip calibration do not.
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "Custom";

    // ── Capture ────────────────────────────────────────────────────────────

    /// <summary>Updates per second sent to the strip.</summary>
    public int FrameRate { get; set; } = 60;

    /// <summary>
    /// Depth of a zone's usual area (until the zone gets its own area), as a fraction of the
    /// screen height.
    /// </summary>
    public double SampleDepth { get; set; } = 0.12;

    /// <summary>Overlap of a zone's usual area (until the zone gets its own area): 0 = none, 1 = double.</summary>
    public double ZoneOverlap { get; set; } = 0.6;

    /// <summary>
    /// The area each zone samples, by zone number (index 0 = zone 1), drawn in Calibration. Null
    /// entries (or a missing entry) use the usual area for the zone's edge.
    /// </summary>
    public List<ZoneArea?> ZoneAreas { get; set; } = [];

    /// <summary>0 = plain average of the zone; 1 = favor the most vivid colors in it.</summary>
    public double ColorFocus { get; set; } = 0.35;

    /// <summary>Ignore letterbox / pillarbox black bars when sampling.</summary>
    public bool DetectBlackBars { get; set; } = true;

    // ── Motion ────────────────────────────────────────────────────────────

    /// <summary>Fade time constant in milliseconds (0 = instant).</summary>
    public double Smoothing { get; set; } = 140;

    /// <summary>Blend each LED with its neighbors (Gaussian radius in LEDs, 0 = off).</summary>
    public double SpatialBlend { get; set; } = 1.2;

    // ── Color ─────────────────────────────────────────────────────────────

    public double Brightness { get; set; } = 1.0;

    public double Saturation { get; set; } = 1.15;

    /// <summary>Extra saturation for muted colors only.</summary>
    public double Vibrance { get; set; } = 0.25;

    public double Contrast { get; set; } = 1.0;

    /// <summary>LED response curve: 1 shows screen values as-is (like DX Light), higher deepens mid-tones.</summary>
    public double Gamma { get; set; } = 1.3;

    /// <summary>Color temperature in Kelvin; 6500 is neutral.</summary>
    public double Temperature { get; set; } = 6500;

    /// <summary>Picture brightness below which LEDs fade to off (0..1 of full brightness).</summary>
    public double BlackThreshold { get; set; } = 0.05;

    // ── Filter (applied to the picture before it is sampled) ─────────────

    /// <summary>Gaussian blur radius as a fraction of the picture height (0 = off).</summary>
    public double FilterBlur { get; set; }

    /// <summary>Pixel block size as a fraction of the picture height (0 = off).</summary>
    public double FilterPixelate { get; set; }

    /// <summary>Hue rotation in degrees.</summary>
    public double FilterHueShift { get; set; }

    /// <summary>Color levels per channel (0 = off, 2–16).</summary>
    public int FilterPosterize { get; set; }

    public bool FilterInvert { get; set; }

    /// <summary>True when any filter changes the picture.</summary>
    public bool HasFilters => FilterBlur > 0 || FilterPixelate > 0 || FilterHueShift != 0 || FilterPosterize > 0 || FilterInvert;

    public Profile Clone()
    {
        var copy = (Profile)MemberwiseClone();
        copy.ZoneAreas = [.. ZoneAreas];
        return copy;
    }

    public ZoneArea? AreaAt(int zone) => zone >= 0 && zone < ZoneAreas.Count ? ZoneAreas[zone] : null;

    /// <summary>Gives zone <paramref name="zone"/> its own area (null: back to the usual area).</summary>
    public void SetArea(int zone, ZoneArea? area)
    {
        while (ZoneAreas.Count <= zone)
        {
            ZoneAreas.Add(null);
        }

        ZoneAreas[zone] = area?.Clamped();
        TrimAreas(int.MaxValue);
    }

    /// <summary>Zone <paramref name="zone"/> was removed: later zones move up one place.</summary>
    public void RemoveZone(int zone)
    {
        if (zone >= 0 && zone < ZoneAreas.Count)
        {
            ZoneAreas.RemoveAt(zone);
        }
    }

    /// <summary>Drops areas of zones that don't exist and trailing empty entries.</summary>
    public void TrimAreas(int zoneCount)
    {
        if (ZoneAreas.Count > zoneCount)
        {
            ZoneAreas.RemoveRange(zoneCount, ZoneAreas.Count - zoneCount);
        }

        while (ZoneAreas.Count > 0 && ZoneAreas[^1] is null)
        {
            ZoneAreas.RemoveAt(ZoneAreas.Count - 1);
        }
    }

    public Profile CloneAs(string name)
    {
        Profile copy = Clone();
        copy.Name = name;
        return copy;
    }

    /// <summary>Brings every value into its valid range (e.g. after loading a hand-edited file).</summary>
    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Custom" : Name.Trim();
        FrameRate = Math.Clamp(FrameRate, 10, 120);
        SampleDepth = Math.Clamp(SampleDepth, 0.01, 0.5);
        ZoneOverlap = Math.Clamp(ZoneOverlap, 0, 3);
        ColorFocus = Math.Clamp(ColorFocus, 0, 1);
        Smoothing = Math.Clamp(Smoothing, 0, 2000);
        SpatialBlend = Math.Clamp(SpatialBlend, 0, 6);
        Brightness = Math.Clamp(Brightness, 0, 1);
        Saturation = Math.Clamp(Saturation, 0, 2.5);
        Vibrance = Math.Clamp(Vibrance, 0, 1);
        Contrast = Math.Clamp(Contrast, 0.5, 2);
        Gamma = Math.Clamp(Gamma, 0.5, 3);
        Temperature = Math.Clamp(Temperature, 2000, 10000);
        BlackThreshold = Math.Clamp(BlackThreshold, 0, 0.4);
        FilterBlur = Math.Clamp(FilterBlur, 0, 0.15);
        FilterPixelate = Math.Clamp(FilterPixelate, 0, 0.15);
        FilterHueShift = double.IsFinite(FilterHueShift) ? Math.Clamp(FilterHueShift, -180, 180) : 0;
        FilterPosterize = FilterPosterize < 2 ? 0 : Math.Min(FilterPosterize, 16);
        ZoneAreas ??= [];
        for (int i = 0; i < ZoneAreas.Count; i++)
        {
            ZoneAreas[i] = ZoneAreas[i] is { } area && double.IsFinite(area.X + area.Y + area.Width + area.Height + area.Overlap) ? area.Clamped() : null;
        }
    }
}
