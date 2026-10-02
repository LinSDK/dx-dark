using System.Numerics;
using DxDark.Capture;
using DxDark.Core.Color;
using DxDark.Core.Effects;
using DxDark.Core.Imaging;
using DxDark.Core.Layout;
using DxDark.Core.Settings;
using DxDark.Core.Sync;

namespace DxDark.App.Infrastructure;

/// <summary>
/// A preview built from a synthetic image (or a built-in effect), used when rendering UI
/// screenshots so the real screen is never captured. Runs the real analysis and color pipeline.
/// </summary>
public static class DemoPreview
{
    /// <param name="shownZone">A zone to show as on the strip (the red dot part-way along it), or -1.</param>
    public static PreviewSnapshot Create(AppSettings settings, int shownZone = -1)
    {
        var source = new CapturedFrame();
        LightingSettings lighting = settings.Lighting;
        if (lighting.Mode == LightMode.Effect && BuiltInEffects.Find(lighting.EffectId) is { } effect)
        {
            ColorMath.TryParseHex(lighting.SolidColor, out byte r, out byte g, out byte b);
            BuiltInEffects.Render(effect.Id, source, 4.0, r, g, b);
        }
        else
        {
            source.FillTestPattern(480, 270);
        }

        LedLayout layout = settings.Layout;
        LedPlacement[] placements = LayoutGeometry.Placements(layout);
        int[] map = LayoutGeometry.PhysicalToLayout(Math.Max(layout.LedCount, 1), placements.Length);
        var content = new PixelRect(0, 0, source.Width, source.Height);
        var profile = settings.GetActiveProfile();

        var analyzer = new FrameAnalyzer();
        analyzer.Load(source);
        var rects = new PixelRect[placements.Length];
        var samples = new Vector3[placements.Length];
        for (int i = 0; i < placements.Length; i++)
        {
            rects[i] = LayoutGeometry.SampleRect(placements[i], content, profile.SampleDepth, profile.ZoneOverlap);
            samples[i] = analyzer.Average(rects[i], profile.ColorFocus);
        }

        var pipeline = new ColorPipeline();
        pipeline.Process(samples, placements, map, layout.IsClosedLoop, profile, settings.Calibration, 1 / 60.0);

        byte[] colors = pipeline.PreviewOutput.ToArray();
        if (shownZone >= 0 && shownZone < layout.Segments.Count)
        {
            var chase = new ZoneChase(layout.SegmentStart(shownZone), layout.Segments[shownZone].LedCount);
            chase.Render(colors, chase.PassSeconds * 0.6);
        }

        var snapshot = new PreviewSnapshot
        {
            Content = content,
            SampleRects = rects,
            Placements = placements,
            LedColors = colors,
            Sending = lighting.Mode is LightMode.ScreenSync or LightMode.Effect,
        };
        PreviewLut.Build(profile, settings.Calibration).Apply(source, snapshot.Frame);
        return snapshot;
    }
}
