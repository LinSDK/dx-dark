using System.Numerics;
using DxDark.Capture;
using DxDark.Core.Color;
using DxDark.Core.Layout;

namespace DxDark.Core.Imaging;

/// <summary>
/// Averages rectangular zones of a frame in linear light. Builds summed-area tables once per frame,
/// so each zone costs the same however large it is, and overlapping zones are free.
/// </summary>
public sealed class FrameAnalyzer
{
    private int _width;
    private int _height;

    // Plain sums of linear light (0..65535 per pixel) and vividness-weighted sums.
    private long[] _r = [], _g = [], _b = [];
    private long[] _w = [], _wr = [], _wg = [], _wb = [];

    public int Width => _width;

    public int Height => _height;

    public void Load(CapturedFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        int stride = w + 1;
        int size = stride * (h + 1);
        if (w != _width || h != _height)
        {
            _width = w;
            _height = h;
            _r = new long[size];
            _g = new long[size];
            _b = new long[size];
            _w = new long[size];
            _wr = new long[size];
            _wg = new long[size];
            _wb = new long[size];
        }

        ushort[] lin = ColorMath.SrgbByteToLinear16;
        byte[] px = frame.Pixels;
        for (int y = 0; y < h; y++)
        {
            long rr = 0, rg = 0, rb = 0, rw = 0, rwr = 0, rwg = 0, rwb = 0;
            int src = y * w * 4;
            int above = y * stride;
            int row = (y + 1) * stride;
            for (int x = 0; x < w; x++, src += 4)
            {
                byte b8 = px[src], g8 = px[src + 1], r8 = px[src + 2];
                long lr = lin[r8], lg = lin[g8], lb = lin[b8];

                // Vividness weight: saturated pixels count far more than gray or dark ones.
                int max = Math.Max(r8, Math.Max(g8, b8));
                int min = Math.Min(r8, Math.Min(g8, b8));
                int chroma = max - min;
                long weight = 1 + (chroma * chroma >> 5);

                rr += lr;
                rg += lg;
                rb += lb;
                rw += weight;
                rwr += lr * weight;
                rwg += lg * weight;
                rwb += lb * weight;

                int i = row + x + 1;
                int j = above + x + 1;
                _r[i] = _r[j] + rr;
                _g[i] = _g[j] + rg;
                _b[i] = _b[j] + rb;
                _w[i] = _w[j] + rw;
                _wr[i] = _wr[j] + rwr;
                _wg[i] = _wg[j] + rwg;
                _wb[i] = _wb[j] + rwb;
            }
        }
    }

    /// <summary>Average linear color of a zone, blended between plain and vividness-weighted mean.</summary>
    public Vector3 Average(PixelRect rect, double colorFocus)
    {
        int x0 = Math.Clamp(rect.X0, 0, _width), x1 = Math.Clamp(rect.X1, 0, _width);
        int y0 = Math.Clamp(rect.Y0, 0, _height), y1 = Math.Clamp(rect.Y1, 0, _height);
        long area = (long)(x1 - x0) * (y1 - y0);
        if (area <= 0)
        {
            return Vector3.Zero;
        }

        int stride = _width + 1;
        int a = y0 * stride + x0, b = y0 * stride + x1, c = y1 * stride + x0, d = y1 * stride + x1;
        const float scale = 1f / 65535f;
        var mean = new Vector3(
            (_r[d] - _r[b] - _r[c] + _r[a]) * scale / area,
            (_g[d] - _g[b] - _g[c] + _g[a]) * scale / area,
            (_b[d] - _b[b] - _b[c] + _b[a]) * scale / area);

        if (colorFocus <= 0)
        {
            return mean;
        }

        long weight = _w[d] - _w[b] - _w[c] + _w[a];
        if (weight <= 0)
        {
            return mean;
        }

        var vivid = new Vector3(
            (_wr[d] - _wr[b] - _wr[c] + _wr[a]) * scale / weight,
            (_wg[d] - _wg[b] - _wg[c] + _wg[a]) * scale / weight,
            (_wb[d] - _wb[b] - _wb[c] + _wb[a]) * scale / weight);
        return Vector3.Lerp(mean, vivid, (float)Math.Clamp(colorFocus, 0, 1));
    }
}
