namespace DxDark.Capture;

/// <summary>
/// A small picture the LEDs are sampled from: the desktop (averaged down on the GPU), a video
/// frame or a generated effect. Tightly packed BGRA, sRGB-encoded, in display orientation.
/// </summary>
public sealed class CapturedFrame
{
    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * 4;

    /// <summary>BGRA pixel data, <see cref="Stride"/> × <see cref="Height"/> bytes.</summary>
    public byte[] Pixels { get; private set; } = [];

    /// <summary>Resolution of the original picture (desktop or video).</summary>
    public int SourceWidth { get; set; }

    public int SourceHeight { get; set; }

    /// <summary>Increases every time the frame content is replaced.</summary>
    public long Sequence { get; set; }

    /// <summary>Makes the frame <paramref name="width"/> × <paramref name="height"/>, reallocating only when the size changes.</summary>
    public void EnsureSize(int width, int height)
    {
        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
        }
    }

    public void CopyTo(CapturedFrame other)
    {
        other.EnsureSize(Width, Height);
        Buffer.BlockCopy(Pixels, 0, other.Pixels, 0, Pixels.Length);
        other.SourceWidth = SourceWidth;
        other.SourceHeight = SourceHeight;
        other.Sequence = Sequence;
    }

    /// <summary>Fills the frame with a synthetic test image (used for UI previews without capture).</summary>
    public void FillTestPattern(int width, int height)
    {
        EnsureSize(width, height);
        SourceWidth = width;
        SourceHeight = height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double hue = (x / (double)width + y / (double)height * 0.25) % 1.0;
                (byte r, byte g, byte b) = HueToRgb(hue, 0.85 - 0.5 * y / height);
                int i = (y * width + x) * 4;
                Pixels[i] = b;
                Pixels[i + 1] = g;
                Pixels[i + 2] = r;
                Pixels[i + 3] = 255;
            }
        }

        Sequence++;
    }

    private static (byte, byte, byte) HueToRgb(double h, double v)
    {
        double r = Math.Clamp(Math.Abs(h * 6 - 3) - 1, 0, 1);
        double g = Math.Clamp(2 - Math.Abs(h * 6 - 2), 0, 1);
        double b = Math.Clamp(2 - Math.Abs(h * 6 - 4), 0, 1);
        return ((byte)(r * v * 255), (byte)(g * v * 255), (byte)(b * v * 255));
    }
}
