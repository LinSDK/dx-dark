using System.Diagnostics;

namespace DxDark.Core.Sync;

/// <summary>
/// Shows one zone on the strip: its LEDs glow dimly (so its extent is visible) while a red dot runs
/// from the zone's first LED to its last, over and over, showing which way the LEDs run.
/// </summary>
public sealed class ZoneChase
{
    private const double PassGap = 0.35;
    private const float Dim = 22;

    private readonly long _started = Stopwatch.GetTimestamp();

    /// <param name="firstLed">First LED of the zone (0-based).</param>
    /// <param name="dotDelaySeconds">How long to show only the zone's extent before the dot starts.</param>
    public ZoneChase(int firstLed, int ledCount, double dotDelaySeconds = 0)
    {
        FirstLed = Math.Max(0, firstLed);
        LedCount = Math.Max(1, ledCount);
        DotDelay = Math.Max(0, dotDelaySeconds);
        PassSeconds = Math.Clamp(LedCount * 0.035, 0.7, 1.8);
    }

    public int FirstLed { get; }

    public int LedCount { get; }

    public double DotDelay { get; }

    /// <summary>Time the dot takes from the first LED to the last.</summary>
    public double PassSeconds { get; }

    public double Elapsed => Stopwatch.GetElapsedTime(_started).TotalSeconds;

    /// <summary>Where the dot is at <paramref name="elapsed"/> seconds: 0 at the zone's first LED, 1 at its last; null between passes.</summary>
    public double? DotProgress(double elapsed)
    {
        double t = elapsed - DotDelay;
        if (t < 0)
        {
            return null;
        }

        double phase = t % (PassSeconds + PassGap);
        return phase <= PassSeconds ? phase / PassSeconds : null;
    }

    /// <summary>Draws the zone at <paramref name="elapsed"/> seconds as R, G, B per LED; all other LEDs are off.</summary>
    public void Render(Span<byte> rgb, double elapsed)
    {
        rgb.Clear();
        int end = Math.Min(rgb.Length / 3, FirstLed + LedCount);
        double head = DotProgress(elapsed) is { } progress ? FirstLed + progress * (LedCount - 1) : double.NaN;
        for (int led = FirstLed; led < end; led++)
        {
            double red = 0;
            if (!double.IsNaN(head))
            {
                double behind = head - led;
                double dot = Math.Abs(behind) < 1 ? 1 - Math.Abs(behind) : 0; // spread over two LEDs so it glides
                double tail = behind > 0 ? 0.45 * Math.Exp(-behind / 2.2) : 0;
                red = Math.Clamp(Math.Max(dot, tail), 0, 1);
            }

            int i = led * 3;
            rgb[i] = (byte)(Dim + (255 - Dim) * red + 0.5);
            rgb[i + 1] = rgb[i + 2] = (byte)(Dim * (1 - red) + 0.5);
        }
    }
}
