using DxDark.Core.Layout;

namespace DxDark.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(32, 110, 35, 20)]
    [InlineData(27, 92, 29, 17)]
    [InlineData(24, 92, 29, 17)]
    [InlineData(34, 116, 41, 17)]
    public void FactoryLayoutsForModel000609(int inches, int leds, int horizontal, int vertical)
    {
        LedLayout layout = StripCatalog.DefaultLayout("000609", inches, leds, 16.0 / 9.0);

        Assert.Equal(leds, layout.LedCount);
        Assert.Collection(
            layout.Segments,
            s => { Assert.Equal(ScreenEdge.Left, s.Edge); Assert.Equal(vertical, s.LedCount); Assert.True(s.Reversed); },
            s => { Assert.Equal(ScreenEdge.Top, s.Edge); Assert.Equal(horizontal, s.LedCount); Assert.False(s.Reversed); },
            s => { Assert.Equal(ScreenEdge.Right, s.Edge); Assert.Equal(vertical, s.LedCount); Assert.False(s.Reversed); },
            s => { Assert.Equal(ScreenEdge.Bottom, s.Edge); Assert.Equal(horizontal, s.LedCount); Assert.True(s.Reversed); });
        Assert.True(layout.IsClosedLoop);
    }

    [Fact]
    public void UnknownModelsAreSplitByAspectRatioAndKeepTheLedCount()
    {
        LedLayout layout = StripCatalog.DefaultLayout("123456", 30, 101, 21.0 / 9.0);
        Assert.Equal(101, layout.LedCount);
        Assert.True(layout.Segments[1].LedCount > layout.Segments[0].LedCount); // wider than tall
    }

    [Fact]
    public void FirstLedOfTheDefaultLayoutIsAtTheBottomOfTheLeftEdge()
    {
        LedPlacement[] placements = LayoutGeometry.Placements(QuickLayout.FourSided(35, 20));
        Assert.Equal(110, placements.Length);
        Assert.Equal(ScreenEdge.Left, placements[0].Edge);
        Assert.True(placements[0].Position > 0.95);   // near the bottom
        Assert.True(placements[19].Position < 0.05);  // top of the left edge
        Assert.Equal(ScreenEdge.Top, placements[20].Edge);
        Assert.True(placements[20].Position < 0.05);  // top-left corner
        Assert.Equal(ScreenEdge.Bottom, placements[109].Edge);
        Assert.True(placements[109].Position < 0.05); // back at the bottom-left corner
    }

    [Theory]
    [InlineData(StartCorner.BottomRight, false, ScreenEdge.Right, true)] // counter-clockwise: up the right side
    [InlineData(StartCorner.TopLeft, true, ScreenEdge.Top, false)]       // clockwise: along the top, left→right
    [InlineData(StartCorner.TopRight, false, ScreenEdge.Top, true)]      // counter-clockwise: along the top, right→left
    public void QuickLayoutStartsWhereAsked(StartCorner corner, bool clockwise, ScreenEdge firstEdge, bool firstReversed)
    {
        LedLayout layout = QuickLayout.Create(corner, clockwise, 10, 5, 10, 5);
        Assert.Equal(firstEdge, layout.Segments[0].Edge);
        Assert.Equal(firstReversed, layout.Segments[0].Reversed);
        Assert.Equal(30, layout.LedCount);
    }

    [Fact]
    public void Version001SettingsKeepTheUsersZones()
    {
        // The user's real 0.0.1 layout: counter-clockwise from the bottom-right.
        const string json = """
            {
              "SchemaVersion": 1,
              "Layout": { "Segments": [
                { "Edge": "Right", "LedCount": 20, "Reversed": true, "Start": 0, "End": 1 },
                { "Edge": "Top", "LedCount": 35, "Reversed": true, "Start": 0, "End": 1 },
                { "Edge": "Left", "LedCount": 20, "Reversed": false, "Start": 0, "End": 1 },
                { "Edge": "Bottom", "LedCount": 35, "Reversed": false, "Start": 0, "End": 1 } ],
                "Offset": 0, "LedsPerMeter": 60 },
              "LayoutIsAutomatic": false,
              "Lighting": { "Mode": "Rhythm", "StaticColor": "#00FF00", "EffectIndex": 2, "EffectSpeed": 50 },
              "General": { "OnExit": "StartEffect", "ExitEffectIndex": 3 },
              "ActiveProfile": "Gaming"
            }
            """;
        var settings = System.Text.Json.JsonSerializer.Deserialize<DxDark.Core.Settings.AppSettings>(json, DxDark.Core.Settings.SettingsStore.JsonOptions)!;
        settings.Normalize();

        Assert.Equal(new[] { ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Left, ScreenEdge.Bottom }, settings.Layout.Segments.Select(s => s.Edge));
        Assert.Equal(new[] { 20, 35, 20, 35 }, settings.Layout.Segments.Select(s => s.LedCount));
        Assert.Equal(new[] { true, true, false, false }, settings.Layout.Segments.Select(s => s.Reversed));
        Assert.Equal(DxDark.Core.Settings.LightMode.ScreenSync, settings.Lighting.Mode); // music mode is gone
        Assert.Equal("#00FF00", settings.Lighting.SolidColor);
        Assert.Equal(1.0, settings.Lighting.EffectSpeed);
        Assert.Equal(DxDark.Core.Settings.ExitAction.TurnOff, settings.General.OnExit);
        Assert.Equal("Gaming", settings.ActiveProfile);
    }

    [Fact]
    public void LedsBeyondTheZonesStayDark()
    {
        Assert.Equal(new[] { 0, 1, 2, -1 }, LayoutGeometry.PhysicalToLayout(4, 3));
    }

    [Fact]
    public void NudgeMovesAZonesSamplingAlongItsEdgeInScreenTerms()
    {
        var plain = new LedLayout { Segments = [new LedSegment { Edge = ScreenEdge.Top, LedCount = 10 }] };
        var nudged = new LedLayout { Segments = [new LedSegment { Edge = ScreenEdge.Top, LedCount = 10, Nudge = 2 }] };
        var reversed = new LedLayout { Segments = [new LedSegment { Edge = ScreenEdge.Top, LedCount = 10, Nudge = 2, Reversed = true }] };

        LedPlacement[] a = LayoutGeometry.Placements(plain);
        LedPlacement[] b = LayoutGeometry.Placements(nudged);
        LedPlacement[] c = LayoutGeometry.Placements(reversed);

        Assert.Equal(a[0].Position + 0.2, b[0].Position, 6);  // two LED widths to the right
        Assert.Equal(b[0].Position, c[9].Position, 6);        // same screen spot whatever the orientation
    }

    [Fact]
    public void NewZonesContinueInTheDirectionTheStripRuns()
    {
        // No zones yet: up the left side, like DX Light's factory wiring.
        Assert.Equal((ScreenEdge.Left, true), LedSegment.Following(null));

        // Clockwise: left (up) → top (left to right) → right (down) → bottom (right to left).
        Assert.Equal((ScreenEdge.Top, false), LedSegment.Following(new LedSegment { Edge = ScreenEdge.Left, Reversed = true }));
        Assert.Equal((ScreenEdge.Bottom, true), LedSegment.Following(new LedSegment { Edge = ScreenEdge.Right, Reversed = false }));

        // Counter-clockwise, like the user's strip: right (up) → top (right to left) → left (down) → bottom (left to right).
        Assert.Equal((ScreenEdge.Top, true), LedSegment.Following(new LedSegment { Edge = ScreenEdge.Right, Reversed = true }));
        Assert.Equal((ScreenEdge.Left, false), LedSegment.Following(new LedSegment { Edge = ScreenEdge.Top, Reversed = true }));
        Assert.Equal((ScreenEdge.Bottom, false), LedSegment.Following(new LedSegment { Edge = ScreenEdge.Left, Reversed = false }));
    }

    [Fact]
    public void ZonesStartWhereThePreviousZoneEnds()
    {
        var layout = new LedLayout
        {
            Segments =
            [
                new LedSegment { Edge = ScreenEdge.Right, LedCount = 20 },
                new LedSegment { Edge = ScreenEdge.Top, LedCount = 35 },
                new LedSegment { Edge = ScreenEdge.Left, LedCount = 20 },
            ],
        };
        Assert.Equal(0, layout.SegmentStart(0));
        Assert.Equal(20, layout.SegmentStart(1));
        Assert.Equal(55, layout.SegmentStart(2));
        Assert.Equal(75, layout.LedCount);
    }

    [Theory]
    [InlineData("000609", 32, 110, 35, 20)]
    [InlineData("000609", 24, 92, 29, 17)]
    [InlineData("unknown", 0, 110, 35, 20)] // estimated from a 16:9 screen
    public void EdgeCountsForKnownAndUnknownStrips(string model, int inches, int leds, int horizontal, int vertical)
    {
        Assert.Equal((horizontal, vertical), StripCatalog.Sides(model, inches, leds, 16.0 / 9.0));
    }

    [Fact]
    public void SampleRectsStayInsideThePictureAndRespectDepth()
    {
        var content = new PixelRect(0, 30, 480, 240); // letterboxed 480×270 frame
        var topLeft = new LedPlacement(ScreenEdge.Top, 0.01, 0.02, 0);
        PixelRect rect = LayoutGeometry.SampleRect(topLeft, content, depth: 0.1, overlap: 0);
        Assert.Equal(30, rect.Y0);
        Assert.Equal(51, rect.Y1); // 10 % of the 210-pixel picture height
        Assert.True(rect.X0 >= 0 && rect.X1 <= 15);

        var right = new LedPlacement(ScreenEdge.Right, 0.5, 0.05, 0);
        PixelRect r = LayoutGeometry.SampleRect(right, content, depth: 0.1, overlap: 1);
        Assert.Equal(480, r.X1);
        Assert.Equal(459, r.X0);
        Assert.True(r.Y0 > 30 && r.Y1 < 240);
    }
}
