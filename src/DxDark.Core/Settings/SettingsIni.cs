using System.Globalization;
using System.Text.RegularExpressions;
using DxDark.Core.Effects;
using DxDark.Core.Layout;
using DxDark.Core.Profiles;

namespace DxDark.Core.Settings;

/// <summary>
/// The settings as an INI file (DXDark.ini): one section per area, values in the units the control
/// panel shows ("12%" rather than 0.12). Reading is forgiving: missing or unknown keys are skipped
/// and every value is brought back into range.
/// </summary>
public static partial class SettingsIni
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Write(AppSettings settings, string version)
    {
        var ini = new IniDocument()
            .HeaderComment($"DX Dark {version} settings.")
            .HeaderComment("DX Dark saves every change here. You can also edit this file; DX Dark reloads it when you save it.");

        ini.Add("General")
            .Comment("Mode: Screen, Effect or Off. Monitor: Primary, or a monitor such as \\\\.\\DISPLAY2. WhenQuitting: TurnOff or KeepColors.")
            .Set("Mode", ModeName(settings.Lighting.Mode))
            .Set("Preset", settings.ActiveProfile)
            .Set("Monitor", settings.MonitorDeviceName ?? "Primary")
            .Set("OffWhenLocked", Bool(settings.General.OffWhenLocked))
            .Set("OffWhenDisplaySleeps", Bool(settings.General.OffWhenDisplayOff))
            .Set("WhenQuitting", settings.General.OnExit == ExitAction.KeepLastColors ? "KeepColors" : "TurnOff")
            .Set("WarnAboutDxLight", Bool(settings.General.WarnAboutDxLight))
            .Set("StartWithWindows", Bool(settings.General.StartWithWindows == true));

        ini.Add("Effect")
            .Comment($"Effect: {string.Join(", ", BuiltInEffects.All.Select(e => e.Id))}, or the Id of a [Video].")
            .Set("Effect", settings.Lighting.EffectId)
            .Set("Speed", Number(settings.Lighting.EffectSpeed) + "x")
            .Set("Color", settings.Lighting.SolidColor);

        CalibrationSettings calibration = settings.Calibration;
        ini.Add("Strip")
            .Comment("Brightness is the strip's own brightness, on top of each preset's. ColorOrder: RGB, RBG, GRB, GBR, BRG or BGR.")
            .Set("Brightness", Percent(calibration.HardwareBrightness / 255.0))
            .Set("PowerLimit", Percent(calibration.PowerLimit))
            .Set("Red", Percent(calibration.RedGain))
            .Set("Green", Percent(calibration.GreenGain))
            .Set("Blue", Percent(calibration.BlueGain))
            .Set("ColorOrder", calibration.ColorOrder.ToString());

        StripMemory strip = settings.Strip;
        if (strip.LedCount > 0)
        {
            ini.Add("Last connected strip")
                .Set("Model", strip.ModelId ?? "")
                .Set("Size", strip.DisplaySizeInches > 0 ? $"{strip.DisplaySizeInches}\"" : "")
                .Set("LEDs", strip.LedCount.ToString(Invariant));
        }

        for (int i = 0; i < settings.Layout.Segments.Count; i++)
        {
            LedSegment zone = settings.Layout.Segments[i];
            IniSection section = ini.Add($"Zone {i + 1}");
            if (i == 0)
            {
                section.Comment("Zones in strip order, from where the cable plugs in. Edge: Left, Top, Right or Bottom.")
                    .Comment("Orientation: LeftToRight or RightToLeft (top, bottom), TopToBottom or BottomToTop (sides).")
                    .Comment("The part of the screen each zone samples is saved in each preset (Zone1Area, Zone2Area...).");
            }

            section
                .Set("Edge", zone.Edge.ToString())
                .Set("LEDs", zone.LedCount.ToString(Invariant))
                .Set("Orientation", OrientationName(zone));
        }

        for (int i = 0; i < settings.Profiles.Count; i++)
        {
            WriteProfile(ini.Add($"Preset {i + 1}"), settings.Profiles[i], explain: i == 0);
        }

        List<VideoEffect> videos;
        lock (settings.Lighting.Videos)
        {
            videos = [.. settings.Lighting.Videos];
        }

        for (int i = 0; i < videos.Count; i++)
        {
            ini.Add($"Video {i + 1}")
                .Set("Name", videos[i].Name)
                .Set("File", videos[i].FileName)
                .Set("Id", videos[i].Id);
        }

        return ini.ToString();
    }

    /// <summary>
    /// Reads settings written by <see cref="Write"/> (or edited by hand). Returns null when the text
    /// holds none of DX Dark's sections, e.g. while an editor is still writing the file.
    /// </summary>
    public static AppSettings? Read(string text)
    {
        IniDocument ini = IniDocument.Parse(text);
        if (ini.Find("General") is null && ini.Find("Strip") is null && !ini.Sections.Any(s => Numbered(s.Name, "Zone") >= 0))
        {
            return null;
        }

        var settings = new AppSettings { Profiles = [] };
        if (ini.Find("General") is { } general)
        {
            if (ParseMode(general.Get("Mode")) is { } mode)
            {
                settings.Lighting.Mode = mode;
            }

            if (general.Get("Preset") is { Length: > 0 } preset)
            {
                settings.ActiveProfile = preset;
            }

            settings.MonitorDeviceName = general.Get("Monitor") is { Length: > 0 } monitor && Simplify(monitor) != "primary" ? monitor : null;
            ReadBool(general, "OffWhenLocked", v => settings.General.OffWhenLocked = v);
            ReadBool(general, "OffWhenDisplaySleeps", v => settings.General.OffWhenDisplayOff = v);
            ReadBool(general, "WarnAboutDxLight", v => settings.General.WarnAboutDxLight = v);
            ReadBool(general, "StartWithWindows", v => settings.General.StartWithWindows = v);
            if (general.Get("WhenQuitting") is { } quitting)
            {
                settings.General.OnExit = Simplify(quitting).StartsWith("keep", StringComparison.Ordinal) ? ExitAction.KeepLastColors : ExitAction.TurnOff;
            }
        }

        if (ini.Find("Effect") is { } effect)
        {
            if (effect.Get("Effect") is { Length: > 0 } id)
            {
                settings.Lighting.EffectId = id;
            }

            ReadNumber(effect, "Speed", v => settings.Lighting.EffectSpeed = v);
            if (effect.Get("Color") is { Length: > 0 } color)
            {
                settings.Lighting.SolidColor = color;
            }
        }

        if (ini.Find("Strip") is { } stripSection)
        {
            CalibrationSettings c = settings.Calibration;
            ReadPercent(stripSection, "Brightness", v => c.HardwareBrightness = (int)Math.Round(v * 255));
            ReadPercent(stripSection, "PowerLimit", v => c.PowerLimit = v);
            ReadPercent(stripSection, "Red", v => c.RedGain = v);
            ReadPercent(stripSection, "Green", v => c.GreenGain = v);
            ReadPercent(stripSection, "Blue", v => c.BlueGain = v);
            if (Enum.TryParse(stripSection.Get("ColorOrder"), ignoreCase: true, out ColorOrder order))
            {
                c.ColorOrder = order;
            }
        }

        if (ini.Find("Last connected strip") is { } last)
        {
            settings.Strip.ModelId = last.Get("Model");
            ReadNumber(last, "Size", v => settings.Strip.DisplaySizeInches = (int)Math.Round(v));
            ReadNumber(last, "LEDs", v => settings.Strip.LedCount = (int)Math.Round(v));
        }

        foreach (IniSection section in Ordered(ini, "Zone"))
        {
            if (!Enum.TryParse(section.Get("Edge"), ignoreCase: true, out ScreenEdge edge) || edge == ScreenEdge.None)
            {
                continue;
            }

            var zone = new LedSegment { Edge = edge, Reversed = LedSegment.ClockwiseReversed(edge) };
            ReadNumber(section, "LEDs", v => zone.LedCount = (int)Math.Round(v));
            ReadNumber(section, "Nudge", v => zone.Nudge = v);
            ReadPercent(section, "SamplingDepth", v => zone.SampleDepth = v);
            ReadPercent(section, "ZoneOverlap", v => zone.ZoneOverlap = v);
            if (ParseReversed(section.Get("Orientation")) is { } reversed)
            {
                zone.Reversed = reversed;
            }

            settings.Layout.Segments.Add(zone);
        }

        int presetNumber = 0;
        foreach (IniSection section in Ordered(ini, "Preset"))
        {
            settings.Profiles.Add(ReadProfile(section, $"Preset {++presetNumber}"));
        }

        foreach (IniSection section in Ordered(ini, "Video"))
        {
            if (section.Get("File") is { Length: > 0 } file)
            {
                var video = new VideoEffect { FileName = file, Name = section.Get("Name") is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(file) };
                if (section.Get("Id") is { Length: > 0 } videoId)
                {
                    video.Id = videoId;
                }

                settings.Lighting.Videos.Add(video);
            }
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>A single preset as its own file, for sharing.</summary>
    public static string WritePreset(Profile profile)
    {
        var ini = new IniDocument().HeaderComment("DX Dark preset");
        WriteProfile(ini.Add("Preset"), profile);
        return ini.ToString();
    }

    public static Profile? ReadPreset(string text)
    {
        IniSection? section = IniDocument.Parse(text).Sections
            .FirstOrDefault(s => s.Name.StartsWith("Preset", StringComparison.OrdinalIgnoreCase));
        if (section is null)
        {
            return null;
        }

        Profile profile = ReadProfile(section, "Imported preset");
        profile.Normalize();
        return profile;
    }

    private static void WriteProfile(IniSection section, Profile p, bool explain = true)
    {
        if (explain)
        {
            section.Comment("SamplingDepth and ZoneOverlap: the usual area of a zone that has no ZoneNArea of its own.")
                .Comment("ZoneNArea: the part of the screen zone N samples (measured from the top-left of the picture) and its overlap.");
        }

        WriteProfileValues(section, p);
        for (int i = 0; i < p.ZoneAreas.Count; i++)
        {
            if (p.ZoneAreas[i] is { } a)
            {
                section.Set($"Zone{i + 1}Area", $"x {Percent(a.X)}, y {Percent(a.Y)}, width {Percent(a.Width)}, height {Percent(a.Height)}, overlap {Percent(a.Overlap)}");
            }
        }
    }

    private static void WriteProfileValues(IniSection section, Profile p) => section
        .Set("Name", p.Name)
        .Set("UpdatesPerSecond", p.FrameRate.ToString(Invariant))
        .Set("SamplingDepth", Percent(p.SampleDepth))
        .Set("ZoneOverlap", Percent(p.ZoneOverlap))
        .Set("ColorFocus", Percent(p.ColorFocus))
        .Set("IgnoreBlackBars", Bool(p.DetectBlackBars))
        .Set("Smoothing", Number(p.Smoothing) + " ms")
        .Set("NeighborBlending", Number(p.SpatialBlend) + " LEDs")
        .Set("Brightness", Percent(p.Brightness))
        .Set("Saturation", Percent(p.Saturation))
        .Set("Vibrance", Percent(p.Vibrance))
        .Set("Contrast", Percent(p.Contrast))
        .Set("Gamma", Number(p.Gamma))
        .Set("Temperature", Number(p.Temperature) + " K")
        .Set("BlackThreshold", Percent(p.BlackThreshold))
        .Set("Blur", Percent(p.FilterBlur))
        .Set("Pixelate", Percent(p.FilterPixelate))
        .Set("HueShift", Number(p.FilterHueShift) + " deg")
        .Set("Posterize", p.FilterPosterize > 0 ? $"{p.FilterPosterize} levels" : "Off")
        .Set("Invert", Bool(p.FilterInvert));

    private static Profile ReadProfile(IniSection section, string fallbackName)
    {
        var p = new Profile { Name = section.Get("Name") is { Length: > 0 } name ? name : fallbackName };
        ReadNumber(section, "UpdatesPerSecond", v => p.FrameRate = (int)Math.Round(v));
        ReadPercent(section, "SamplingDepth", v => p.SampleDepth = v);
        ReadPercent(section, "ZoneOverlap", v => p.ZoneOverlap = v);
        ReadPercent(section, "ColorFocus", v => p.ColorFocus = v);
        ReadBool(section, "IgnoreBlackBars", v => p.DetectBlackBars = v);
        ReadNumber(section, "Smoothing", v => p.Smoothing = v);
        ReadNumber(section, "NeighborBlending", v => p.SpatialBlend = v);
        ReadPercent(section, "Brightness", v => p.Brightness = v);
        ReadPercent(section, "Saturation", v => p.Saturation = v);
        ReadPercent(section, "Vibrance", v => p.Vibrance = v);
        ReadPercent(section, "Contrast", v => p.Contrast = v);
        ReadNumber(section, "Gamma", v => p.Gamma = v);
        ReadNumber(section, "Temperature", v => p.Temperature = v);
        ReadPercent(section, "BlackThreshold", v => p.BlackThreshold = v);
        ReadPercent(section, "Blur", v => p.FilterBlur = v);
        ReadPercent(section, "Pixelate", v => p.FilterPixelate = v);
        ReadNumber(section, "HueShift", v => p.FilterHueShift = v);
        if (section.Get("Posterize") is { } posterize)
        {
            p.FilterPosterize = TryNumber(posterize, out double levels) ? (int)Math.Round(levels) : 0;
        }

        ReadBool(section, "Invert", v => p.FilterInvert = v);
        for (int zone = 1; zone <= 64; zone++)
        {
            if (section.Get($"Zone{zone}Area") is { } text)
            {
                double[] n = Numbers().Matches(text).Select(m => double.Parse(m.Value.Replace(',', '.'), Invariant)).ToArray();
                if (n.Length >= 4)
                {
                    p.SetArea(zone - 1, new ZoneArea(n[0] / 100, n[1] / 100, n[2] / 100, n[3] / 100, n.Length > 4 ? n[4] / 100 : p.ZoneOverlap));
                }
            }
        }
        return p;
    }

    // ── Values ──────────────────────────────────────────────────────────────

    private static string Number(double value) => value.ToString("0.###", Invariant);

    private static string Percent(double fraction) => (fraction * 100).ToString("0.##", Invariant) + "%";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string ModeName(LightMode mode) => mode switch
    {
        LightMode.Effect => "Effect",
        LightMode.Off => "Off",
        LightMode.Controller => "StripButtons",
        _ => "Screen",
    };

    private static LightMode? ParseMode(string? text) => Simplify(text) switch
    {
        "screen" or "screensync" => LightMode.ScreenSync,
        "effect" => LightMode.Effect,
        "off" => LightMode.Off,
        "stripbuttons" or "controller" => LightMode.Controller,
        _ => null,
    };

    private static string OrientationName(LedSegment zone) => LedSegment.IsHorizontal(zone.Edge)
        ? (zone.Reversed ? "RightToLeft" : "LeftToRight")
        : (zone.Reversed ? "BottomToTop" : "TopToBottom");

    /// <summary>True for right→left / bottom→top, false for left→right / top→bottom.</summary>
    private static bool? ParseReversed(string? text) => Simplify(text) switch
    {
        "righttoleft" or "bottomtotop" or "up" or "left" or "reversed" => true,
        "lefttoright" or "toptobottom" or "down" or "right" or "normal" => false,
        _ => null,
    };

    private static void ReadNumber(IniSection section, string key, Action<double> set)
    {
        if (TryNumber(section.Get(key), out double value))
        {
            set(value);
        }
    }

    private static void ReadPercent(IniSection section, string key, Action<double> set)
    {
        if (TryNumber(section.Get(key), out double value))
        {
            set(value / 100);
        }
    }

    private static void ReadBool(IniSection section, string key, Action<bool> set)
    {
        switch (Simplify(section.Get(key)))
        {
            case "true" or "yes" or "on" or "1":
                set(true);
                break;
            case "false" or "no" or "off" or "0":
                set(false);
                break;
        }
    }

    /// <summary>The number at the start of a value such as "12%", "140 ms" or "1,5" (units are ignored).</summary>
    internal static bool TryNumber(string? text, out double value)
    {
        value = 0;
        Match match = text is null ? Match.Empty : LeadingNumber().Match(text);
        return match.Success
            && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, Invariant, out value)
            && double.IsFinite(value);
    }

    /// <summary>Lower case without spaces, dashes or underscores: "Right to left" → "righttoleft".</summary>
    private static string Simplify(string? text) =>
        string.Concat((text ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>Sections named "Zone 1", "Zone 2"… in number order.</summary>
    private static IEnumerable<IniSection> Ordered(IniDocument ini, string prefix) => ini.Sections
        .Select(s => (Section: s, Number: Numbered(s.Name, prefix)))
        .Where(x => x.Number >= 0)
        .OrderBy(x => x.Number)
        .Select(x => x.Section);

    private static int Numbered(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(name.AsSpan(prefix.Length).Trim(), NumberStyles.None, Invariant, out int n) ? n : -1;
    }

    [GeneratedRegex(@"^\s*[+-]?(\d+([.,]\d*)?|[.,]\d+)")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"[+-]?\d+(\.\d+)?")]
    private static partial Regex Numbers();
}
