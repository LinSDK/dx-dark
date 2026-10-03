using DxDark.Capture;
using DxDark.Core.Imaging;
using DxDark.Core.Layout;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;

namespace DxDark.Tests;

public class FilterAndSamplingTests
{
    private static CapturedFrame Solid(int w, int h, byte r, byte g, byte b)
    {
        var frame = new CapturedFrame();
        frame.FillTestPattern(w, h);
        for (int i = 0; i < frame.Pixels.Length; i += 4)
        {
            frame.Pixels[i] = b;
            frame.Pixels[i + 1] = g;
            frame.Pixels[i + 2] = r;
            frame.Pixels[i + 3] = 255;
        }

        return frame;
    }

    private static (byte R, byte G, byte B) Pixel(CapturedFrame f, int x, int y)
    {
        int i = (y * f.Width + x) * 4;
        return (f.Pixels[i + 2], f.Pixels[i + 1], f.Pixels[i]);
    }

    [Fact]
    public void WithoutFiltersThePictureIsUnchanged()
    {
        CapturedFrame source = Solid(32, 18, 10, 200, 90);
        var target = new CapturedFrame();
        new FrameFilters().Apply(source, target, new Profile());
        Assert.Equal(source.Pixels, target.Pixels);
    }

    [Fact]
    public void InvertAndHueShiftChangeColors()
    {
        var target = new CapturedFrame();
        new FrameFilters().Apply(Solid(8, 8, 200, 100, 0), target, new Profile { FilterInvert = true });
        Assert.Equal((55, 155, 255), Pixel(target, 3, 3));

        // A third of the way around the color wheel: red becomes green.
        new FrameFilters().Apply(Solid(8, 8, 255, 0, 0), target, new Profile { FilterHueShift = 120 });
        (byte r, byte g, byte b) = Pixel(target, 3, 3);
        Assert.True(g > 240 && r < 15 && b < 15, $"{r},{g},{b}");
    }

    [Fact]
    public void PosterizeKeepsOnlyAFewLevels()
    {
        var target = new CapturedFrame();
        new FrameFilters().Apply(Solid(8, 8, 100, 30, 220), target, new Profile { FilterPosterize = 2 });
        Assert.Equal((0, 0, 255), Pixel(target, 0, 0));
    }

    [Fact]
    public void BlurSoftensAnEdgeButKeepsFlatAreas()
    {
        CapturedFrame source = Solid(64, 36, 0, 0, 0);
        for (int y = 0; y < 36; y++)
        {
            for (int x = 32; x < 64; x++)
            {
                int i = (y * 64 + x) * 4;
                source.Pixels[i] = source.Pixels[i + 1] = source.Pixels[i + 2] = 255;
            }
        }

        var target = new CapturedFrame();
        new FrameFilters().Apply(source, target, new Profile { FilterBlur = 0.05 });
        Assert.InRange(Pixel(target, 31, 10).R, 40, 215); // the edge is now a gradient
        Assert.Equal(0, Pixel(target, 2, 10).R);
        Assert.Equal(255, Pixel(target, 61, 10).R);
    }

    [Fact]
    public void PixelateMakesBlocksOfOneColor()
    {
        CapturedFrame source = Solid(40, 40, 0, 0, 0);
        source.Pixels[0] = source.Pixels[1] = source.Pixels[2] = 250; // one bright pixel in the first block
        var target = new CapturedFrame();
        new FrameFilters().Apply(source, target, new Profile { FilterPixelate = 0.1 }); // 4-pixel blocks
        Assert.Equal(Pixel(target, 0, 0), Pixel(target, 3, 3));
        Assert.Equal(0, Pixel(target, 4, 4).R);
    }

    [Fact]
    public void BlackBarsSurviveAFadeToBlackAndBack()
    {
        // Letterboxed picture: bars in rows 0–11 and 78–89.
        CapturedFrame Letterbox(byte level)
        {
            var frame = new CapturedFrame();
            frame.FillTestPattern(160, 90);
            for (int y = 0; y < 90; y++)
            {
                for (int x = 0; x < 160; x++)
                {
                    int i = (y * 160 + x) * 4;
                    byte v = y is < 12 or >= 78 ? (byte)0 : level;
                    frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = v;
                }
            }

            return frame;
        }

        var detector = new BlackBarDetector();
        double t = 0;
        PixelRect area = default;
        for (; t < 2; t += 0.1)
        {
            area = detector.Update(Letterbox(180), t, enabled: true);
        }

        Assert.Equal(12, area.Y0);

        // A dark scene where only a fifth of each picture row is still lit (this used to drop the
        // bars), then full black, then the picture again.
        CapturedFrame dim = Letterbox(180);
        for (int y = 12; y < 78; y++)
        {
            Array.Clear(dim.Pixels, (y * 160 + 32) * 4, 128 * 4);
        }

        for (; t < 5; t += 0.1)
        {
            area = detector.Update(dim, t, enabled: true);
            Assert.Equal(12, area.Y0);
        }

        for (; t < 7; t += 0.1)
        {
            area = detector.Update(Letterbox(0), t, enabled: true);
            Assert.Equal(12, area.Y0);
        }

        area = detector.Update(Letterbox(180), t, enabled: true);
        Assert.Equal(12, area.Y0); // the bars were never lost, so nothing has to come back
    }

    [Fact]
    public void ZonesUseThePresetsAreaOrTheWholeEdge()
    {
        var layout = new LedLayout
        {
            Segments =
            [
                new LedSegment { Edge = ScreenEdge.Top, LedCount = 10 },
                new LedSegment { Edge = ScreenEdge.Right, LedCount = 10 },
            ],
        };
        var preset = new Profile { SampleDepth = 0.12, ZoneOverlap = 0.6 };
        preset.SetArea(0, new ZoneArea(0.1, 0.05, 0.8, 0.3, 0));

        ZoneArea[] areas = LayoutGeometry.Areas(layout, preset, 16.0 / 9.0);
        Assert.Equal(new ZoneArea(0.1, 0.05, 0.8, 0.3, 0), areas[0]);
        Assert.Equal(0.12 * 9 / 16, areas[1].Width, 6); // the usual area: 12 % of the height deep
        Assert.Equal(1 - 0.12 * 9 / 16, areas[1].X, 6);
        Assert.Equal((0.0, 1.0, 0.6), (areas[1].Y, areas[1].Height, areas[1].Overlap));
    }

    [Fact]
    public void EachLedSamplesItsShareOfTheZonesArea()
    {
        var layout = new LedLayout { Segments = [new LedSegment { Edge = ScreenEdge.Top, LedCount = 4 }] };
        LedPlacement[] leds = LayoutGeometry.Placements(layout);
        var content = new PixelRect(0, 0, 400, 200);
        var area = new ZoneArea(0.5, 0.25, 0.5, 0.5, 0); // the right half, from a quarter down

        PixelRect first = LayoutGeometry.SampleRect(leds[0], area, content);
        PixelRect last = LayoutGeometry.SampleRect(leds[3], area, content);
        Assert.Equal(new PixelRect(200, 50, 250, 150), first);
        Assert.Equal(new PixelRect(350, 50, 400, 150), last);
    }

    [Fact]
    public void PresetsKeepTheirOwnAreasAndCopiesDontShareThem()
    {
        var preset = new Profile();
        preset.SetArea(2, new ZoneArea(0, 0, 1, 0.2, 0.5));
        Profile copy = preset.CloneAs("Copy");
        copy.SetArea(2, new ZoneArea(0, 0, 1, 0.4, 0.5));

        Assert.Equal(0.2, preset.AreaAt(2)!.Value.Height, 6);
        Assert.Null(preset.AreaAt(0));
        preset.RemoveZone(0); // zone 3 becomes zone 2
        Assert.Equal(0.2, preset.AreaAt(1)!.Value.Height, 6);
    }

    [Fact]
    public void ZoneAreasAndFiltersSurviveTheIniFile()
    {
        var settings = new AppSettings();
        settings.Layout.Segments.Add(new LedSegment { Edge = ScreenEdge.Top, LedCount = 35 });
        settings.Layout.Segments.Add(new LedSegment { Edge = ScreenEdge.Left, LedCount = 20 });
        Profile p = settings.GetActiveProfile();
        p.SetArea(0, new ZoneArea(0.05, -0.1, 0.9, 0.25, 1.2));
        p.FilterBlur = 0.025;
        p.FilterPixelate = 0.05;
        p.FilterHueShift = -45;
        p.FilterPosterize = 6;
        p.FilterInvert = true;

        string ini = SettingsIni.Write(settings, "test");
        Assert.Contains("Zone1Area = x 5%, y -10%, width 90%, height 25%, overlap 120%", ini);
        AppSettings copy = SettingsIni.Read(ini)!;

        Profile q = copy.GetActiveProfile();
        ZoneArea area = q.AreaAt(0)!.Value;
        Assert.Equal((0.05, -0.1, 0.9, 0.25, 1.2), (Math.Round(area.X, 6), Math.Round(area.Y, 6), Math.Round(area.Width, 6), Math.Round(area.Height, 6), Math.Round(area.Overlap, 6)));
        Assert.Null(q.AreaAt(1));
        Assert.Equal(0.025, q.FilterBlur, 6);
        Assert.Equal(0.05, q.FilterPixelate, 6);
        Assert.Equal(-45, q.FilterHueShift);
        Assert.Equal(6, q.FilterPosterize);
        Assert.True(q.FilterInvert);
    }
}
