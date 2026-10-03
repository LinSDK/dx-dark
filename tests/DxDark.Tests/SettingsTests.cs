using DxDark.Core.Effects;
using DxDark.Core.Layout;
using DxDark.Core.Platform;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;

namespace DxDark.Tests;

public sealed class SettingsTests : IDisposable
{
    // The user's real 0.0.2 settings.json, shortened to two presets.
    private const string Version002Json = """
        {
          "SchemaVersion": 2,
          "Layout": {
            "Segments": [
              { "Edge": "Right", "LedCount": 20, "Reversed": true, "Nudge": -4 },
              { "Edge": "Top", "LedCount": 35, "Reversed": true, "Nudge": 0 },
              { "Edge": "Left", "LedCount": 20, "Reversed": false, "Nudge": 0 },
              { "Edge": "Bottom", "LedCount": 35, "Reversed": false, "Nudge": -3 }
            ],
            "LedsPerMeter": 60
          },
          "LayoutIsAutomatic": false,
          "Calibration": { "RedGain": 1, "GreenGain": 1, "BlueGain": 1, "ColorOrder": "RGB", "PowerLimit": 0.3333, "HardwareBrightness": 51 },
          "Lighting": { "Mode": "ScreenSync", "EffectId": "rainbow", "EffectSpeed": 1, "StaticColor": "#FF7A2E", "Videos": [] },
          "General": { "OnExit": "TurnOff", "OffWhenLocked": true, "OffWhenDisplayOff": true, "WarnAboutDxLight": true },
          "Profiles": [
            { "Name": "Balanced", "FrameRate": 60, "SampleDepth": 0.12, "ZoneOverlap": 0.6, "ColorFocus": 0.35, "DetectBlackBars": true,
              "Smoothing": 0, "SpatialBlend": 1.2, "Brightness": 1, "Saturation": 1.15, "Vibrance": 0.25, "Contrast": 1, "Gamma": 1.3,
              "Temperature": 6500, "BlackThreshold": 0.05 },
            { "Name": "Cinema", "FrameRate": 60, "SampleDepth": 0.04, "ZoneOverlap": 1, "ColorFocus": 0.25, "DetectBlackBars": true,
              "Smoothing": 20, "SpatialBlend": 0.5, "Brightness": 1, "Saturation": 1.1, "Vibrance": 1, "Contrast": 1.3, "Gamma": 2.2,
              "Temperature": 6000, "BlackThreshold": 0.06 }
          ],
          "ActiveProfile": "Cinema",
          "MonitorDeviceName": null
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dxdark-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Version002SettingsAreConvertedToTheIniFileAndKeptAsABackup()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), Version002Json);
        using var store = new SettingsStore(_folder);
        store.Load();

        Assert.False(store.IsNew);
        Assert.True(File.Exists(Path.Combine(_folder, "DXDark.ini")));
        Assert.False(File.Exists(Path.Combine(_folder, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_folder, "settings.0.0.2-backup.json")));

        // Everything survives a fresh read of the INI file.
        AppSettings s = SettingsIni.Read(File.ReadAllText(store.FilePath))!;
        Assert.Equal(new[] { ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Left, ScreenEdge.Bottom }, s.Layout.Segments.Select(z => z.Edge));
        Assert.Equal(new[] { 20, 35, 20, 35 }, s.Layout.Segments.Select(z => z.LedCount));
        Assert.Equal(new[] { true, true, false, false }, s.Layout.Segments.Select(z => z.Reversed));
        // The nudges became areas in every preset: zone 1 (right side, 20 LEDs) moved up 4 LEDs,
        // zone 4 (bottom, 35 LEDs) 3 LEDs to the left; zones without a nudge use the whole edge.
        Assert.All(s.Layout.Segments, z => Assert.Equal(0, z.Nudge));
        foreach (Profile preset in s.Profiles)
        {
            Assert.Equal(-4.0 / 20, preset.AreaAt(0)!.Value.Y, 4); // stored to 0.01 %
            Assert.Null(preset.AreaAt(1));
            Assert.Null(preset.AreaAt(2));
            Assert.Equal(-3.0 / 35, preset.AreaAt(3)!.Value.X, 4);
        }

        Assert.Equal(0.04 * 9 / 16, s.Profiles.Single(p => p.Name == "Cinema").AreaAt(0)!.Value.Width, 4);
        Assert.Equal(51, s.Calibration.HardwareBrightness);
        Assert.Equal(0.3333, s.Calibration.PowerLimit, 4);
        Assert.Equal("Cinema", s.ActiveProfile);
        Profile cinema = s.Profiles.Single(p => p.Name == "Cinema");
        Assert.Equal(0.04, cinema.SampleDepth, 6);
        Assert.Equal(2.2, cinema.Gamma, 6);
        Assert.Equal(1.3, cinema.Contrast, 6);
        Assert.Equal(0, s.Profiles.Single(p => p.Name == "Balanced").Smoothing);
    }

    [Fact]
    public void EverySettingSurvivesTheIniFile()
    {
        var original = new AppSettings();
        original.Lighting.Mode = LightMode.Effect;
        original.Lighting.EffectId = BuiltInEffects.Comet;
        original.Lighting.EffectSpeed = 1.75;
        original.Lighting.SolidColor = "#12AB34";
        original.Lighting.Videos.Add(new VideoEffect { Id = "abc123", Name = "Beach = sunset; loop", FileName = "abc123.mp4" });
        original.General.OnExit = ExitAction.KeepLastColors;
        original.General.OffWhenLocked = false;
        original.General.WarnAboutDxLight = false;
        original.MonitorDeviceName = @"\\.\DISPLAY2";
        original.Calibration.RedGain = 0.9;
        original.Calibration.BlueGain = 0.85;
        original.Calibration.ColorOrder = ColorOrder.GRB;
        original.Calibration.PowerLimit = 0.5;
        original.Calibration.HardwareBrightness = 128;
        original.Strip = new StripMemory { ModelId = "000609", DisplaySizeInches = 27, LedCount = 92 };
        original.Layout.Segments.Add(new LedSegment { Edge = ScreenEdge.Bottom, LedCount = 29, Reversed = true });
        original.Layout.Segments.Add(new LedSegment { Edge = ScreenEdge.Left, LedCount = 17, Reversed = true });
        original.Profiles = [BuiltInProfiles.Find("Vivid")!.CloneAs("  My [odd] = preset ")];
        original.Profiles[0].Smoothing = 333;
        original.Profiles[0].DetectBlackBars = false;
        original.ActiveProfile = original.Profiles[0].Name;
        original.Normalize();

        AppSettings copy = SettingsIni.Read(SettingsIni.Write(original, "test"))!;

        Assert.Equal(LightMode.Effect, copy.Lighting.Mode);
        Assert.Equal(BuiltInEffects.Comet, copy.Lighting.EffectId);
        Assert.Equal(1.75, copy.Lighting.EffectSpeed);
        Assert.Equal("#12AB34", copy.Lighting.SolidColor);
        VideoEffect video = Assert.Single(copy.Lighting.Videos);
        Assert.Equal(("abc123", "Beach = sunset; loop", "abc123.mp4"), (video.Id, video.Name, video.FileName));
        Assert.Equal(ExitAction.KeepLastColors, copy.General.OnExit);
        Assert.False(copy.General.OffWhenLocked);
        Assert.True(copy.General.OffWhenDisplayOff);
        Assert.False(copy.General.WarnAboutDxLight);
        Assert.Equal(@"\\.\DISPLAY2", copy.MonitorDeviceName);
        Assert.Equal((0.9, 1.0, 0.85), (copy.Calibration.RedGain, copy.Calibration.GreenGain, copy.Calibration.BlueGain));
        Assert.Equal(ColorOrder.GRB, copy.Calibration.ColorOrder);
        Assert.Equal(0.5, copy.Calibration.PowerLimit);
        Assert.Equal(128, copy.Calibration.HardwareBrightness);
        Assert.Equal(("000609", 27, 92), (copy.Strip.ModelId, copy.Strip.DisplaySizeInches, copy.Strip.LedCount));
        Assert.Collection(
            copy.Layout.Segments,
            z => Assert.Equal((ScreenEdge.Bottom, 29, true), (z.Edge, z.LedCount, z.Reversed)),
            z => Assert.Equal((ScreenEdge.Left, 17, true), (z.Edge, z.LedCount, z.Reversed)));
        Profile preset = Assert.Single(copy.Profiles);
        Assert.Equal("My [odd] = preset", preset.Name);
        Assert.Equal(preset.Name, copy.ActiveProfile);
        Assert.Equal(333, preset.Smoothing);
        Assert.False(preset.DetectBlackBars);
        Profile vivid = BuiltInProfiles.Find("Vivid")!;
        Assert.Equal(vivid.Saturation, preset.Saturation, 6);
        Assert.Equal(vivid.ColorFocus, preset.ColorFocus, 6);
        Assert.Equal(vivid.Gamma, preset.Gamma, 6);
    }

    [Fact]
    public void HandEditedFilesAreReadForgivingly()
    {
        const string ini = """
            ; edited by hand
            [general]
            mode = effect
            PRESET=Cinema

            [Zone 2]
            Edge = top
            LEDs = 35
            Orientation = right to left

            [Zone 1]
            Edge = Right
            LEDs = 20 LEDs
            Orientation = Bottom to top
            Nudge = -1,5

            [Zone 3]
            Edge = Nowhere

            [Preset 1]
            Name = Cinema
            SamplingDepth = 18 %
            Smoothing = 320ms
            Temperature = 6000 K
            Brightness = 250%
            IgnoreBlackBars = no
            """;

        AppSettings s = SettingsIni.Read(ini)!;

        Assert.Equal(LightMode.Effect, s.Lighting.Mode);
        Assert.Equal("Cinema", s.ActiveProfile);
        Assert.Collection(
            s.Layout.Segments, // ordered by number; the zone with an unknown edge is skipped
            z => Assert.Equal((ScreenEdge.Right, 20, true), (z.Edge, z.LedCount, z.Reversed)),
            z => Assert.Equal((ScreenEdge.Top, 35, true), (z.Edge, z.LedCount, z.Reversed)));
        Profile p = Assert.Single(s.Profiles);
        Assert.Equal(0.18, p.SampleDepth, 6);
        Assert.Equal(320, p.Smoothing);
        Assert.Equal(6000, p.Temperature);
        Assert.Equal(1, p.Brightness); // brought back into range
        Assert.False(p.DetectBlackBars);
    }

    [Fact]
    public void AFirstRunHasNoZonesEvenNextToAnOldSettingsBackup()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.0.0.2-backup.json"), Version002Json);
        using var store = new SettingsStore(_folder);
        store.Load();
        Assert.True(store.IsNew);
        Assert.Empty(store.Current.Layout.Segments);
    }

    [Fact]
    public void AStaleSettingsJsonIsRetiredSoItCannotBringOldZonesBack()
    {
        File.WriteAllText(Path.Combine(_folder, "DXDark.ini"), SettingsIni.Write(new AppSettings(), "test"));
        File.WriteAllText(Path.Combine(_folder, "settings.json"), Version002Json);
        using (var store = new SettingsStore(_folder))
        {
            store.Load();
            Assert.Empty(store.Current.Layout.Segments);
        }

        Assert.False(File.Exists(Path.Combine(_folder, "settings.json")));
        File.Delete(Path.Combine(_folder, "DXDark.ini"));
        using var fresh = new SettingsStore(_folder);
        fresh.Load();
        Assert.True(fresh.IsNew);
        Assert.Empty(fresh.Current.Layout.Segments);
    }

    [Fact]
    public void DeletingTheSettingsFileWhileRunningStartsOver()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), Version002Json);
        using var store = new SettingsStore(_folder);
        store.Load();
        using var changed = new ManualResetEventSlim(false);
        AppSettings? reported = null;
        store.ChangedOnDisk += s =>
        {
            reported = s;
            changed.Set();
        };
        store.StartWatching();

        File.Delete(store.FilePath);
        Assert.True(changed.Wait(5000));
        Assert.Empty(reported!.Layout.Segments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartWithWindowsLivesInTheIniFile(bool on)
    {
        var settings = new AppSettings();
        settings.General.StartWithWindows = on;
        string ini = SettingsIni.Write(settings, "test");
        Assert.Contains($"StartWithWindows = {(on ? "true" : "false")}", ini);
        Assert.Equal(on, SettingsIni.Read(ini)!.General.StartWithWindows);
        Assert.Null(SettingsIni.Read("[General]\nMode = Screen")!.General.StartWithWindows); // not set yet
    }

    [Fact]
    public void ThePrimaryMonitorIsWrittenByName()
    {
        string ini = SettingsIni.Write(new AppSettings(), "test");
        Assert.Contains("Monitor = Primary", ini);
        Assert.Null(SettingsIni.Read(ini)!.MonitorDeviceName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("; just a comment")]
    [InlineData("[Somewhere else]\nKey = value")]
    public void FilesWithoutDxDarkSettingsAreIgnored(string text) => Assert.Null(SettingsIni.Read(text));

    [Fact]
    public void EditsMadeOutsideDxDarkAreReported()
    {
        using var store = new SettingsStore(_folder);
        store.Load();
        Assert.True(store.IsNew);
        Assert.True(File.Exists(store.FilePath)); // written straight away, so it can be found and edited

        using var changed = new ManualResetEventSlim(false);
        AppSettings? reported = null;
        store.ChangedOnDisk += s =>
        {
            reported = s;
            changed.Set();
        };
        store.StartWatching();

        // DX Dark's own saves are not reported back.
        store.Current.ActiveProfile = "Vivid";
        store.SaveNow();
        Assert.False(changed.Wait(1200));

        string text = File.ReadAllText(store.FilePath).Replace("Preset = Vivid", "Preset = Gaming");
        File.WriteAllText(store.FilePath, text);
        Assert.True(changed.Wait(5000));
        Assert.Equal("Gaming", reported!.ActiveProfile);
    }

    [Fact]
    public void PresetsExportAsIniAndImportFromIniOrOldJson()
    {
        Profile preset = BuiltInProfiles.Find("Gaming")!.CloneAs("Shared");
        string iniPath = Path.Combine(_folder, "Shared.ini");
        SettingsStore.ExportProfile(preset, iniPath);
        Profile fromIni = SettingsStore.ImportProfile(iniPath)!;
        Assert.Equal("Shared", fromIni.Name);
        Assert.Equal(preset.Smoothing, fromIni.Smoothing);
        Assert.Equal(preset.SampleDepth, fromIni.SampleDepth, 6);

        string jsonPath = Path.Combine(_folder, "Old.json");
        File.WriteAllText(jsonPath, """{ "Name": "Old", "Smoothing": 77, "SampleDepth": 0.2 }""");
        Profile fromJson = SettingsStore.ImportProfile(jsonPath)!;
        Assert.Equal(("Old", 77.0, 0.2), (fromJson.Name, fromJson.Smoothing, fromJson.SampleDepth));
    }

    [Fact]
    public void StartupShortcutPointsAtTheProgram()
    {
        string link = Path.Combine(_folder, "Startup", "DX Dark.lnk");
        string target = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        StartupShortcut.SetEnabled(true, target, link);
        Assert.True(StartupShortcut.IsEnabled(link));
        Assert.Equal(target, StartupShortcut.ReadTarget(link), ignoreCase: true);

        StartupShortcut.SetEnabled(false, target, link);
        Assert.False(StartupShortcut.IsEnabled(link));
    }
}
