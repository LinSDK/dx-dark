using System.Numerics;
using DxDark.Capture;
using DxDark.Core.Color;
using DxDark.Core.Imaging;
using DxDark.Core.Layout;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;
using DxDark.Core.Sync;

namespace DxDark.Tests;

public class PipelineTests
{
    private static Profile Neutral() => new()
    {
        Saturation = 1,
        Vibrance = 0,
        Contrast = 1,
        Gamma = 1,
        Temperature = 6500,
        Brightness = 1,
        BlackThreshold = 0,
        Smoothing = 0,
        SpatialBlend = 0,
    };

    private static (LedPlacement[] Placements, int[] Map) Strip(int n)
    {
        var layout = new LedLayout { Segments = [new LedSegment { Edge = ScreenEdge.Top, LedCount = n }] };
        return (LayoutGeometry.Placements(layout), LayoutGeometry.PhysicalToLayout(n, n));
    }

    private static Vector3 Linear(byte r, byte g, byte b) =>
        new(ColorMath.SrgbByteToLinear[r], ColorMath.SrgbByteToLinear[g], ColorMath.SrgbByteToLinear[b]);

    [Fact]
    public void NeutralSettingsPassScreenColorsThrough()
    {
        (LedPlacement[] placements, int[] map) = Strip(3);
        var pipeline = new ColorPipeline();
        Vector3[] samples = [Linear(200, 100, 50), Linear(0, 0, 0), Linear(255, 255, 255)];
        var calibration = new CalibrationSettings { PowerLimit = 1 };

        pipeline.Process(samples, placements, map, false, Neutral(), calibration, 1 / 60.0);

        byte[] o = pipeline.Output.ToArray();
        Assert.InRange(o[0], 199, 201);
        Assert.InRange(o[1], 99, 101);
        Assert.InRange(o[2], 49, 51);
        Assert.Equal(new byte[] { 0, 0, 0 }, o[3..6]);
        Assert.Equal(new byte[] { 255, 255, 255 }, o[6..9]);
    }

    [Fact]
    public void PowerLimitCapsTheWholeStripLikeDxLight()
    {
        (LedPlacement[] placements, int[] map) = Strip(10);
        var pipeline = new ColorPipeline();
        Vector3[] white = Enumerable.Repeat(Vector3.One, 10).ToArray();

        pipeline.Process(white, placements, map, false, Neutral(), new CalibrationSettings(), 1 / 60.0);

        // All-white at the default 1/3 budget = DX Light's per-LED cap of R+G+B ≤ 255.
        Assert.All(pipeline.Output.ToArray(), b => Assert.InRange(b, 84, 86));
    }

    [Fact]
    public void PowerLimitLetsASingleHighlightStayBright()
    {
        (LedPlacement[] placements, int[] map) = Strip(10);
        var pipeline = new ColorPipeline();
        Vector3[] samples = new Vector3[10];
        samples[4] = Vector3.One;

        pipeline.Process(samples, placements, map, false, Neutral(), new CalibrationSettings(), 1 / 60.0);

        Assert.Equal(new byte[] { 255, 255, 255 }, pipeline.Output.ToArray()[12..15]);
    }

    [Fact]
    public void SmoothingFadesTowardsTheTargetAndSettles()
    {
        (LedPlacement[] placements, int[] map) = Strip(1);
        var pipeline = new ColorPipeline();
        Profile profile = Neutral();
        profile.Smoothing = 100;
        var calibration = new CalibrationSettings { PowerLimit = 1 };

        pipeline.Process([Vector3.Zero], placements, map, false, profile, calibration, 1 / 60.0);
        pipeline.Process([Vector3.One], placements, map, false, profile, calibration, 1 / 60.0);
        byte afterOneFrame = pipeline.Output[0];
        Assert.InRange(afterOneFrame, 1, 200);

        for (int i = 0; i < 120; i++)
        {
            pipeline.Process([Vector3.One], placements, map, false, profile, calibration, 1 / 60.0);
        }

        Assert.Equal(255, pipeline.Output[0]);
        Assert.True(pipeline.Converged);
    }

    [Theory]
    [InlineData(110)]
    [InlineData(92)]
    [InlineData(30)]
    public void SyncFramesFitOnePacketAndCoverEveryLed(int leds)
    {
        int[] bounds = DxDark.Protocol.LightStrip.ZoneBoundaries(leds);
        var output = new byte[leds * 3];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = (byte)(i * 7); // all different, so no zones merge
        }

        DxDark.Protocol.LedRange[] ranges = [];
        int count = SyncEngine.BuildRanges(output, new CalibrationSettings(), bounds, ref ranges);

        Assert.InRange(count, 1, DxDark.Protocol.Packet.MaxRangesPerSyncPacket);
        Assert.Equal(1, ranges[0].First);
        Assert.Equal(leds, ranges[count - 1].Last);
        for (int i = 1; i < count; i++)
        {
            Assert.Equal(ranges[i - 1].Last + 1, ranges[i].First);
            Assert.InRange(ranges[i].Last - ranges[i].First + 1, 1, 3);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void SmoothingLooksTheSameAtAnyUpdateRate(int hz)
    {
        // After the same amount of time, a fade must reach the same brightness whether the screen
        // updates 30 or 240 times a second.
        (LedPlacement[] placements, int[] map) = Strip(1);
        var pipeline = new ColorPipeline();
        Profile profile = Neutral();
        profile.Smoothing = 200;
        profile.Gamma = 1;
        var calibration = new CalibrationSettings { PowerLimit = 1 };

        pipeline.Process([Vector3.Zero], placements, map, false, profile, calibration, 1.0 / hz);
        for (int i = 0; i < hz / 5; i++) // 200 ms of updates
        {
            pipeline.Process([Vector3.One], placements, map, false, profile, calibration, 1.0 / hz);
        }

        // One time constant: 1 - e^-1 ≈ 63 % in linear light ≈ 207/255 after sRGB encoding.
        Assert.InRange(pipeline.Output[0], 203, 211);
    }

    [Fact]
    public void EveryFrameHasTheSameFiftyZonesEvenWhenColorsRepeat()
    {
        // Regression: merging equal neighbors made frame sizes change from frame to frame (e.g. a
        // small object moving over a dark screen), which made the start of the strip flicker.
        int[] bounds = DxDark.Protocol.LightStrip.ZoneBoundaries(110);
        DxDark.Protocol.LedRange[] ranges = [];

        int uniform = SyncEngine.BuildRanges(new byte[110 * 3], new CalibrationSettings(), bounds, ref ranges);
        var mixed = new byte[110 * 3];
        mixed[60] = 255;
        int withObject = SyncEngine.BuildRanges(mixed, new CalibrationSettings(), bounds, ref ranges);

        Assert.Equal(50, uniform);
        Assert.Equal(50, withObject);
    }

    [Fact]
    public void PreviewLutMatchesThePipeline()
    {
        Profile profile = BuiltInProfiles.Find("Vivid")!.Clone();
        var calibration = new CalibrationSettings { PowerLimit = 1 };
        PreviewLut lut = PreviewLut.Build(profile, calibration);

        var source = new CapturedFrame();
        source.EnsureSize(1, 1);
        source.Pixels[0] = 40; source.Pixels[1] = 120; source.Pixels[2] = 200; // B, G, R
        var target = new CapturedFrame();
        lut.Apply(source, target);

        Vector3 expected = Vector3.Clamp(
            ColorPipeline.Adjust(ColorPipeline.ApplyBlackThreshold(Linear(200, 120, 40), (float)profile.BlackThreshold), profile, ColorMath.TemperatureGains(profile.Temperature)),
            Vector3.Zero, Vector3.One) * 255;
        Assert.InRange(target.Pixels[2], expected.X - 4, expected.X + 4);
        Assert.InRange(target.Pixels[1], expected.Y - 4, expected.Y + 4);
        Assert.InRange(target.Pixels[0], expected.Z - 4, expected.Z + 4);
    }

    [Fact]
    public void BuiltInEffectsDrawEveryPixel()
    {
        var frame = new CapturedFrame();
        foreach (DxDark.Core.Effects.BuiltInEffect effect in DxDark.Core.Effects.BuiltInEffects.All)
        {
            DxDark.Core.Effects.BuiltInEffects.Render(effect.Id, frame, 2.5, 255, 120, 40);
            Assert.Equal(DxDark.Core.Effects.BuiltInEffects.Width, frame.Width);
            Assert.All(Enumerable.Range(0, frame.Width * frame.Height), i => Assert.Equal(255, frame.Pixels[i * 4 + 3]));
            Assert.True(frame.Pixels.Where((_, i) => i % 4 != 3).Any(b => b > 0), $"{effect.Name} is all black");
        }
    }

    [Fact]
    public void ZoneChaseRunsTheDotFromTheZonesFirstLedToItsLast()
    {
        var chase = new ZoneChase(firstLed: 20, ledCount: 35);
        var rgb = new byte[110 * 3];

        int BrightestRed(double t)
        {
            chase.Render(rgb, t);
            int best = -1, bestRed = -1;
            for (int led = 0; led < 110; led++)
            {
                if (rgb[led * 3] > bestRed)
                {
                    bestRed = rgb[led * 3];
                    best = led;
                }
            }

            return best;
        }

        int early = BrightestRed(chase.PassSeconds * 0.1);
        int middle = BrightestRed(chase.PassSeconds * 0.5);
        int late = BrightestRed(chase.PassSeconds * 0.95);
        Assert.InRange(early, 20, 26);
        Assert.InRange(middle, 35, 40);
        Assert.InRange(late, 49, 54);

        // Only the zone lights up.
        chase.Render(rgb, chase.PassSeconds * 0.5);
        Assert.All(Enumerable.Range(0, 20).Concat(Enumerable.Range(55, 55)), led => Assert.Equal(0, rgb[led * 3] + rgb[led * 3 + 1] + rgb[led * 3 + 2]));
        Assert.All(Enumerable.Range(20, 35), led => Assert.True(rgb[led * 3] > 0));

        // It keeps running: a pause between passes, then the same pass again, for as long as the zone is selected.
        Assert.Null(chase.DotProgress(chase.PassSeconds + 0.1));
        Assert.Equal(chase.DotProgress(chase.PassSeconds * 0.5)!.Value, chase.DotProgress(100 * (chase.PassSeconds + 0.35) + chase.PassSeconds * 0.5)!.Value, 6);
    }

    [Fact]
    public void ASmallObjectOnADarkScreenIsNotMistakenForBlackBars()
    {
        // The Chase test pattern: a bright block at the bottom middle of a black screen. Its dark
        // sides used to be taken for pillarbox bars, so the left and right zones sampled the block.
        var frame = new CapturedFrame();
        frame.FillTestPattern(160, 90);
        Array.Clear(frame.Pixels);
        for (int y = 78; y < 90; y++)
        {
            for (int x = 70; x < 90; x++)
            {
                int i = (y * 160 + x) * 4;
                frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = 230;
            }
        }

        var detector = new BlackBarDetector();
        PixelRect picture = default;
        for (double t = 0; t < 3; t += 0.1)
        {
            picture = detector.Update(frame, t, enabled: true);
        }

        Assert.Equal(new PixelRect(0, 0, 160, 90), picture);
    }

    [Fact]
    public void ZoneChaseWaitsForTheLedCountToSettle()
    {
        var chase = new ZoneChase(0, 20, dotDelaySeconds: 0.6);
        Assert.Null(chase.DotProgress(0.3));
        Assert.NotNull(chase.DotProgress(0.7));
    }

    [Fact]
    public void ShownZonesKeepEachLedsColorInTheUsualFiftyRanges()
    {
        int[] bounds = DxDark.Protocol.LightStrip.ZoneBoundaries(110);
        var chase = new ZoneChase(20, 35);
        var rgb = new byte[110 * 3];
        chase.Render(rgb, chase.PassSeconds * 0.37);
        DxDark.Protocol.LedRange[] ranges = [];

        int count = SyncEngine.BuildExactRanges(rgb, new CalibrationSettings(), bounds, ref ranges);

        Assert.Equal(50, count); // same size as every other frame
        Assert.Equal(1, ranges[0].First);
        Assert.Equal(110, ranges[count - 1].Last);
        for (int r = 0; r < count; r++)
        {
            if (r > 0)
            {
                Assert.Equal(ranges[r - 1].Last + 1, ranges[r].First);
            }

            for (int led = ranges[r].First - 1; led < ranges[r].Last; led++)
            {
                Assert.Equal((rgb[led * 3], rgb[led * 3 + 1], rgb[led * 3 + 2]), (ranges[r].R, ranges[r].G, ranges[r].B));
            }
        }
    }

    [Fact]
    public void BusyFramesFallBackToZoneAverages()
    {
        int[] bounds = DxDark.Protocol.LightStrip.ZoneBoundaries(110);
        var output = new byte[110 * 3];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = (byte)(i * 13); // every LED different
        }

        DxDark.Protocol.LedRange[] ranges = [];
        Assert.Equal(50, SyncEngine.BuildExactRanges(output, new CalibrationSettings(), bounds, ref ranges));
    }

    [Fact]
    public void RainbowsTravelSidewaysAndUpwards()
    {
        var frame = new CapturedFrame();
        int W = DxDark.Core.Effects.BuiltInEffects.Width, H = DxDark.Core.Effects.BuiltInEffects.Height;
        (byte, byte, byte) Pixel(int x, int y) => (frame.Pixels[(y * W + x) * 4 + 2], frame.Pixels[(y * W + x) * 4 + 1], frame.Pixels[(y * W + x) * 4]);

        DxDark.Core.Effects.BuiltInEffects.Render(DxDark.Core.Effects.BuiltInEffects.RainbowSweep, frame, 1.0, 0, 0, 0);
        Assert.Equal(Pixel(10, 0), Pixel(10, H - 1)); // every column is one color
        Assert.NotEqual(Pixel(10, 0), Pixel(W / 2, 0));

        DxDark.Core.Effects.BuiltInEffects.Render(DxDark.Core.Effects.BuiltInEffects.RainbowRise, frame, 1.0, 0, 0, 0);
        Assert.Equal(Pixel(0, 10), Pixel(W - 1, 10)); // every row is one color
        Assert.NotEqual(Pixel(0, 10), Pixel(0, H / 2));
    }

    [Fact]
    public void TheThunderstormThumbnailShowsLightning()
    {
        var calm = new CapturedFrame();
        var flash = new CapturedFrame();
        string storm = DxDark.Core.Effects.BuiltInEffects.Storm;
        DxDark.Core.Effects.BuiltInEffects.Render(storm, flash, DxDark.Core.Effects.BuiltInEffects.ThumbnailTime(storm), 0, 0, 0);
        DxDark.Core.Effects.BuiltInEffects.Render(storm, calm, DxDark.Core.Effects.BuiltInEffects.ThumbnailTime(storm) + 0.45, 0, 0, 0);
        static long Light(CapturedFrame f) => f.Pixels.Where((_, i) => i % 4 != 3).Sum(b => (long)b);
        Assert.True(Light(flash) > Light(calm) * 1.2);
    }

    [Fact]
    public void QuantizerIgnoresTinyWobble()
    {
        Assert.Equal(10, ColorPipeline.Quantize(10.6f, 10));
        Assert.Equal(10, ColorPipeline.Quantize(9.4f, 10));
        Assert.Equal(12, ColorPipeline.Quantize(11.8f, 10));
        Assert.Equal(0, ColorPipeline.Quantize(0.6f, 0));
    }

    [Fact]
    public void AnalyzerAveragesUniformFrames()
    {
        var frame = new CapturedFrame();
        frame.FillTestPattern(40, 20);
        for (int i = 0; i < frame.Pixels.Length; i += 4)
        {
            frame.Pixels[i] = 50;      // B
            frame.Pixels[i + 1] = 100; // G
            frame.Pixels[i + 2] = 200; // R
        }

        var analyzer = new FrameAnalyzer();
        analyzer.Load(frame);
        Vector3 avg = analyzer.Average(new PixelRect(3, 2, 30, 15), colorFocus: 0.5);

        Vector3 expected = Linear(200, 100, 50);
        Assert.InRange(avg.X, expected.X - 0.001f, expected.X + 0.001f);
        Assert.InRange(avg.Y, expected.Y - 0.001f, expected.Y + 0.001f);
        Assert.InRange(avg.Z, expected.Z - 0.001f, expected.Z + 0.001f);
    }

    [Fact]
    public void BlackBarsAreDetectedAfterTheyStayStable()
    {
        var frame = new CapturedFrame();
        frame.FillTestPattern(160, 90);
        for (int y = 0; y < 90; y++)
        {
            for (int x = 0; x < 160; x++)
            {
                int i = (y * 160 + x) * 4;
                byte v = y is < 12 or >= 78 ? (byte)0 : (byte)180;
                frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = v;
            }
        }

        var detector = new BlackBarDetector();
        PixelRect first = detector.Update(frame, 0.0, enabled: true);
        Assert.Equal(0, first.Y0); // not applied immediately

        detector.Update(frame, 0.5, enabled: true);
        PixelRect later = detector.Update(frame, 1.2, enabled: true);
        Assert.Equal(12, later.Y0);
        Assert.Equal(78, later.Y1);
        Assert.Equal(0, later.X0);
    }
}
