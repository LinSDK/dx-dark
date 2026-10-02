using System.Numerics;
using DxDark.Core.Color;
using DxDark.Core.Layout;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;

namespace DxDark.Core.Sync;

/// <summary>
/// Turns sampled zone colors into LED values:
/// black threshold → blend with neighbors → smooth over time → picture adjustments →
/// power limit → 8-bit values with a small dead band (so LEDs never flicker between two levels).
/// </summary>
public sealed class ColorPipeline
{
    private Vector3[] _thresholded = [];
    private Vector3[] _blended = [];
    private Vector3[] _smoothed = [];
    private Vector3[] _drive = [];
    private byte[] _output = [];
    private byte[] _previewOutput = [];
    private float[] _kernel = [];
    private double _kernelSigma = -1;
    private double _temperatureFor = -1;
    private Vector3 _temperatureGains = Vector3.One;
    private bool _hasHistory;

    /// <summary>R, G, B per physical LED, after all processing (before channel reordering).</summary>
    public ReadOnlySpan<byte> Output => _output;

    /// <summary>
    /// R, G, B per physical LED before the power limit: the colors as intended, matching the
    /// processed preview image. The strip receives <see cref="Output"/>.
    /// </summary>
    public ReadOnlySpan<byte> PreviewOutput => _previewOutput;

    /// <summary>How much the power limit dimmed the last frame (1 = not at all).</summary>
    public float PowerScale { get; private set; } = 1;

    /// <summary>True once smoothing has settled, i.e. the output no longer changes.</summary>
    public bool Converged { get; private set; }

    public void Reset() => _hasHistory = false;

    public void Process(
        ReadOnlySpan<Vector3> samples,
        LedPlacement[] placements,
        int[] physicalToLayout,
        bool closedLoop,
        Profile profile,
        CalibrationSettings calibration,
        double dtSeconds)
    {
        int n = samples.Length;
        if (_smoothed.Length != n)
        {
            _thresholded = new Vector3[n];
            _blended = new Vector3[n];
            _smoothed = new Vector3[n];
            _hasHistory = false;
        }

        // 1. Fade dark zones out smoothly instead of switching them off at a hard cut-off.
        float threshold = (float)profile.BlackThreshold;
        for (int i = 0; i < n; i++)
        {
            _thresholded[i] = placements[i].IsMapped ? ApplyBlackThreshold(samples[i], threshold) : Vector3.Zero;
        }

        // 2. Blend neighbors so moving objects glide along the strip instead of jumping.
        Blend(_thresholded, _blended, placements, closedLoop, profile.SpatialBlend);

        // 3. Smooth over time (frame-rate independent exponential fade).
        float alpha = !_hasHistory || profile.Smoothing <= 0
            ? 1f
            : 1f - (float)Math.Exp(-dtSeconds * 1000.0 / profile.Smoothing);
        float largestChange = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 delta = _blended[i] - _smoothed[i];
            _smoothed[i] += delta * alpha;
            largestChange = MathF.Max(largestChange, ColorMath.MaxComponent(Vector3.Abs(delta)));
        }

        _hasHistory = true;
        Converged = largestChange < 0.002f;

        // 4. Map to physical LEDs and apply the picture settings and strip calibration.
        int m = physicalToLayout.Length;
        if (_drive.Length != m)
        {
            _drive = new Vector3[m];
            _output = new byte[m * 3];
            _previewOutput = new byte[m * 3];
        }

        if (profile.Temperature != _temperatureFor)
        {
            _temperatureFor = profile.Temperature;
            _temperatureGains = ColorMath.TemperatureGains(profile.Temperature);
        }

        var gains = new Vector3((float)calibration.RedGain, (float)calibration.GreenGain, (float)calibration.BlueGain);
        float total = 0;
        for (int p = 0; p < m; p++)
        {
            int q = physicalToLayout[p];
            Vector3 c = q < 0 || q >= n || !placements[q].IsMapped
                ? Vector3.Zero
                : Vector3.Clamp(Adjust(_smoothed[q], profile, _temperatureGains) * gains, Vector3.Zero, Vector3.One);
            _drive[p] = c;
            total += c.X + c.Y + c.Z;
        }

        // 5. Power limit over the whole strip: bright highlights may exceed DX Light's per-LED cap
        //    as long as the total stays within the same budget.
        float budget = (float)(calibration.PowerLimit * 3 * m);
        float scale = total > budget && total > 0 ? budget / total : 1f;
        PowerScale = scale;

        // 6. 8-bit output with hysteresis.
        for (int p = 0; p < m; p++)
        {
            Vector3 v = _drive[p] * (scale * 255f);
            int o = p * 3;
            _output[o] = Quantize(v.X, _output[o]);
            _output[o + 1] = Quantize(v.Y, _output[o + 1]);
            _output[o + 2] = Quantize(v.Z, _output[o + 2]);

            Vector3 intended = _drive[p] * 255f;
            _previewOutput[o] = (byte)(intended.X + 0.5f);
            _previewOutput[o + 1] = (byte)(intended.Y + 0.5f);
            _previewOutput[o + 2] = (byte)(intended.Z + 0.5f);
        }
    }

    /// <summary>Fades colors darker than <paramref name="threshold"/> (display-encoded luma) smoothly to black.</summary>
    public static Vector3 ApplyBlackThreshold(Vector3 linear, float threshold)
    {
        if (threshold <= 0)
        {
            return linear;
        }

        float y = ColorMath.Encode(Vector3.Dot(linear, ColorMath.Luma));
        return linear * SmoothStep(threshold * 0.4f, threshold, y);
    }

    /// <summary>Picture adjustments on a linear color; returns LED drive values (0..1, may exceed before clamping).</summary>
    public static Vector3 Adjust(Vector3 linear, Profile profile, Vector3 temperatureGains)
    {
        // Work on display-encoded values so the sliders behave like a monitor's picture settings.
        Vector3 s = ColorMath.Encode(linear);

        if (profile.Contrast != 1)
        {
            s = Vector3.Max((s - new Vector3(0.5f)) * (float)profile.Contrast + new Vector3(0.5f), Vector3.Zero);
        }

        if (profile.Saturation != 1 || profile.Vibrance != 0)
        {
            float luma = Vector3.Dot(s, ColorMath.Luma);
            float chroma = Math.Clamp(ColorMath.MaxComponent(s) - ColorMath.MinComponent(s), 0f, 1f);
            float amount = (float)(profile.Saturation * (1 + profile.Vibrance * (1 - chroma)));
            s = Vector3.Max(new Vector3(luma) + (s - new Vector3(luma)) * amount, Vector3.Zero);
            float max = ColorMath.MaxComponent(s);
            if (max > 1)
            {
                s /= max; // keep the hue when boosting pushes a channel past full
            }
        }

        s *= temperatureGains;

        if (profile.Gamma != 1)
        {
            float g = (float)profile.Gamma;
            s = new Vector3(MathF.Pow(s.X, g), MathF.Pow(s.Y, g), MathF.Pow(s.Z, g));
        }

        return s * (float)profile.Brightness;
    }

    private void Blend(Vector3[] source, Vector3[] target, LedPlacement[] placements, bool closedLoop, double sigma)
    {
        int n = source.Length;
        if (sigma < 0.05 || n < 2)
        {
            Array.Copy(source, target, n);
            return;
        }

        if (sigma != _kernelSigma)
        {
            _kernelSigma = sigma;
            int radius = Math.Min(18, (int)Math.Ceiling(sigma * 3));
            _kernel = new float[radius + 1];
            for (int k = 0; k <= radius; k++)
            {
                _kernel[k] = (float)Math.Exp(-(k * k) / (2 * sigma * sigma));
            }
        }

        bool wrap = closedLoop && placements.All(p => p.IsMapped);
        for (int i = 0; i < n; i++)
        {
            if (!placements[i].IsMapped)
            {
                target[i] = Vector3.Zero;
                continue;
            }

            Vector3 sum = source[i] * _kernel[0];
            float weight = _kernel[0];
            bool left = true, right = true;
            for (int k = 1; k < _kernel.Length && (left || right); k++)
            {
                float w = _kernel[k];
                if (left)
                {
                    int j = i - k;
                    if (j < 0 && wrap)
                    {
                        j += n;
                    }

                    if (j >= 0 && placements[j].IsMapped)
                    {
                        sum += source[j] * w;
                        weight += w;
                    }
                    else
                    {
                        left = false; // stop at gaps and at the ends of an open chain
                    }
                }

                if (right)
                {
                    int j = i + k;
                    if (j >= n && wrap)
                    {
                        j -= n;
                    }

                    if (j < n && placements[j].IsMapped)
                    {
                        sum += source[j] * w;
                        weight += w;
                    }
                    else
                    {
                        right = false;
                    }
                }
            }

            target[i] = sum / weight;
        }
    }

    internal static byte Quantize(float value, byte previous)
    {
        float v = Math.Clamp(value, 0f, 255f);
        if (MathF.Abs(v - previous) < 0.75f)
        {
            return previous;
        }

        return (byte)MathF.Round(v);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
        {
            return x >= edge1 ? 1f : 0f;
        }

        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
