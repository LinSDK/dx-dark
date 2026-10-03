using DxDark.Capture;
using DxDark.Core.Profiles;

namespace DxDark.Core.Imaging;

/// <summary>
/// The preset's picture filters, applied to the picture before it is sampled (so the strip and the
/// live view both show them): pixelate, blur, hue shift, posterize and invert. Frames are small
/// (at most ~480×270), so this is cheap.
/// </summary>
public sealed class FrameFilters
{
    private int[] _sums = [];
    private byte[] _scratch = [];

    /// <summary>Writes <paramref name="source"/> with the filters of <paramref name="profile"/> into <paramref name="target"/>.</summary>
    public void Apply(CapturedFrame source, CapturedFrame target, Profile profile)
    {
        source.CopyTo(target);
        if (!profile.HasFilters || target.Width == 0 || target.Height == 0)
        {
            return;
        }

        int w = target.Width, h = target.Height;
        byte[] px = target.Pixels;

        if (profile.FilterPixelate > 0)
        {
            Pixelate(px, w, h, Math.Max(2, (int)Math.Round(profile.FilterPixelate * h)));
        }

        if (profile.FilterBlur > 0)
        {
            // Three box blurs approximate a Gaussian with this standard deviation.
            double sigma = profile.FilterBlur * h;
            int radius = Math.Max(1, (int)Math.Round(Math.Sqrt(sigma * sigma + 1) - 0.5));
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(px, w, h, radius);
            }
        }

        if (profile.FilterHueShift != 0 || profile.FilterPosterize > 0 || profile.FilterInvert)
        {
            ColorFilters(px, w * h, profile.FilterHueShift, profile.FilterPosterize, profile.FilterInvert);
        }
    }

    private static void Pixelate(byte[] px, int w, int h, int block)
    {
        for (int by = 0; by < h; by += block)
        {
            for (int bx = 0; bx < w; bx += block)
            {
                int x1 = Math.Min(w, bx + block), y1 = Math.Min(h, by + block);
                int b = 0, g = 0, r = 0, n = 0;
                for (int y = by; y < y1; y++)
                {
                    for (int x = bx, i = (y * w + bx) * 4; x < x1; x++, i += 4)
                    {
                        b += px[i];
                        g += px[i + 1];
                        r += px[i + 2];
                        n++;
                    }
                }

                byte mb = (byte)(b / n), mg = (byte)(g / n), mr = (byte)(r / n);
                for (int y = by; y < y1; y++)
                {
                    for (int x = bx, i = (y * w + bx) * 4; x < x1; x++, i += 4)
                    {
                        px[i] = mb;
                        px[i + 1] = mg;
                        px[i + 2] = mr;
                    }
                }
            }
        }
    }

    /// <summary>A horizontal then a vertical moving average with edge clamping.</summary>
    private void BoxBlur(byte[] px, int w, int h, int radius)
    {
        if (_scratch.Length != px.Length)
        {
            _scratch = new byte[px.Length];
        }

        Pass(px, _scratch, w, h, radius, horizontal: true);
        Pass(_scratch, px, w, h, radius, horizontal: false);
    }

    private void Pass(byte[] from, byte[] to, int w, int h, int radius, bool horizontal)
    {
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4;
        if (_sums.Length < 3)
        {
            _sums = new int[3];
        }

        int window = 2 * radius + 1;
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * w * 4 : line * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    sum += from[start + Math.Clamp(k, 0, length - 1) * step + c];
                }

                for (int p = 0; p < length; p++)
                {
                    to[start + p * step + c] = (byte)(sum / window);
                    int leaving = Math.Clamp(p - radius, 0, length - 1);
                    int entering = Math.Clamp(p + radius + 1, 0, length - 1);
                    sum += from[start + entering * step + c] - from[start + leaving * step + c];
                }
            }

            for (int p = 0; p < length; p++)
            {
                to[start + p * step + 3] = 255;
            }
        }
    }

    private static void ColorFilters(byte[] px, int count, double hueDegrees, int levels, bool invert)
    {
        // Hue rotation about the grey axis (keeps brightness close to the original).
        double a = hueDegrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double k = (1 - cos) / 3, s = Math.Sqrt(1.0 / 3) * sin;
        float m00 = (float)(cos + k), m01 = (float)(k - s), m02 = (float)(k + s);
        float m10 = (float)(k + s), m11 = (float)(cos + k), m12 = (float)(k - s);
        float m20 = (float)(k - s), m21 = (float)(k + s), m22 = (float)(cos + k);
        bool rotate = hueDegrees != 0;
        float levelStep = levels > 1 ? 255f / (levels - 1) : 0;

        for (int n = 0, i = 0; n < count; n++, i += 4)
        {
            float b = px[i], g = px[i + 1], r = px[i + 2];
            if (rotate)
            {
                (r, g, b) = (m00 * r + m01 * g + m02 * b, m10 * r + m11 * g + m12 * b, m20 * r + m21 * g + m22 * b);
            }

            if (levels > 1)
            {
                r = MathF.Round(Math.Clamp(r, 0, 255) / levelStep) * levelStep;
                g = MathF.Round(Math.Clamp(g, 0, 255) / levelStep) * levelStep;
                b = MathF.Round(Math.Clamp(b, 0, 255) / levelStep) * levelStep;
            }

            if (invert)
            {
                (r, g, b) = (255 - r, 255 - g, 255 - b);
            }

            px[i] = (byte)Math.Clamp(b + 0.5f, 0, 255);
            px[i + 1] = (byte)Math.Clamp(g + 0.5f, 0, 255);
            px[i + 2] = (byte)Math.Clamp(r + 0.5f, 0, 255);
        }
    }
}
