using System.Numerics;
using DxDark.Capture;
using DxDark.Core.Color;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;

namespace DxDark.Core.Sync;

/// <summary>
/// A 3D color lookup table holding the preset's color processing, so the live preview image can
/// show exactly what the color sliders do (brightness, saturation, gamma, temperature…) at a
/// fraction of the cost of running every pixel through the pipeline.
/// </summary>
public sealed class PreviewLut
{
    private const int N = 33; // grid points per channel

    private static readonly int[] Index = new int[256];
    private static readonly int[] Fraction = new int[256]; // 0..256

    private readonly byte[] _table = new byte[N * N * N * 3];

    static PreviewLut()
    {
        for (int v = 0; v < 256; v++)
        {
            int position = v * (N - 1) * 256 / 255;
            Index[v] = Math.Min(N - 2, position >> 8);
            Fraction[v] = position - Index[v] * 256;
        }
    }

    private PreviewLut()
    {
    }

    /// <summary>Builds the table for a preset and the strip's calibration.</summary>
    public static PreviewLut Build(Profile profile, CalibrationSettings calibration)
    {
        var lut = new PreviewLut();
        Vector3 temperature = ColorMath.TemperatureGains(profile.Temperature);
        var gains = new Vector3((float)calibration.RedGain, (float)calibration.GreenGain, (float)calibration.BlueGain);
        int i = 0;
        for (int r = 0; r < N; r++)
        {
            for (int g = 0; g < N; g++)
            {
                for (int b = 0; b < N; b++)
                {
                    var linear = new Vector3(
                        ColorMath.Decode(r / (N - 1f)),
                        ColorMath.Decode(g / (N - 1f)),
                        ColorMath.Decode(b / (N - 1f)));
                    linear = ColorPipeline.ApplyBlackThreshold(linear, (float)profile.BlackThreshold);
                    Vector3 drive = Vector3.Clamp(ColorPipeline.Adjust(linear, profile, temperature) * gains, Vector3.Zero, Vector3.One);
                    lut._table[i++] = (byte)(drive.X * 255 + 0.5f);
                    lut._table[i++] = (byte)(drive.Y * 255 + 0.5f);
                    lut._table[i++] = (byte)(drive.Z * 255 + 0.5f);
                }
            }
        }

        return lut;
    }

    /// <summary>Writes the processed version of <paramref name="source"/> into <paramref name="target"/>.</summary>
    public void Apply(CapturedFrame source, CapturedFrame target)
    {
        target.EnsureSize(source.Width, source.Height);
        target.SourceWidth = source.SourceWidth;
        target.SourceHeight = source.SourceHeight;
        byte[] src = source.Pixels, dst = target.Pixels;
        byte[] t = _table;
        const int sr = N * N * 3, sg = N * 3, sb = 3;

        for (int p = 0; p < src.Length; p += 4)
        {
            int b8 = src[p], g8 = src[p + 1], r8 = src[p + 2];
            int ri = Index[r8], gi = Index[g8], bi = Index[b8];
            int fr = Fraction[r8], fg = Fraction[g8], fb = Fraction[b8];
            int o = ri * sr + gi * sg + bi * sb;

            // Trilinear interpolation between the eight surrounding grid points (fixed point).
            int w000 = (256 - fr) * (256 - fg) >> 8;
            int w010 = (256 - fr) * fg >> 8;
            int w100 = fr * (256 - fg) >> 8;
            int w110 = fr * fg >> 8;
            for (int c = 0; c < 3; c++)
            {
                int c00 = t[o + c] * (256 - fb) + t[o + sb + c] * fb;
                int c01 = t[o + sg + c] * (256 - fb) + t[o + sg + sb + c] * fb;
                int c10 = t[o + sr + c] * (256 - fb) + t[o + sr + sb + c] * fb;
                int c11 = t[o + sr + sg + c] * (256 - fb) + t[o + sr + sg + sb + c] * fb;
                int value = (c00 * w000 + c01 * w010 + c10 * w100 + c11 * w110) >> 16;
                dst[p + 2 - c] = (byte)Math.Min(255, value); // c: 0 = R, 1 = G, 2 = B; BGRA order
            }

            dst[p + 3] = 255;
        }

        // Every processed image gets a new number from one counter, so a viewer comparing numbers
        // never mistakes a new picture for one it has already shown (snapshots are pooled).
        target.Sequence = Interlocked.Increment(ref _imageSequence);
    }

    private static long _imageSequence;
}
