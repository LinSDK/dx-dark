using DxDark.Capture;

namespace DxDark.Core.Effects;

/// <summary>A built-in effect: an animated picture generated in code, sampled like the screen.</summary>
/// <param name="UsesColor">The effect is drawn in the user's chosen solid color.</param>
public sealed record BuiltInEffect(string Id, string Name, bool UsesColor = false);

/// <summary>
/// The built-in effect canvases. Like a video, each is a moving picture; the strip shows its edges,
/// exactly as with screen sync, so presets, zones and calibration apply to effects too.
/// </summary>
public static class BuiltInEffects
{
    public const string Rainbow = "rainbow";
    public const string RainbowSweep = "rainbow-sweep";
    public const string RainbowRise = "rainbow-rise";
    public const string Spectrum = "spectrum";
    public const string Aurora = "aurora";
    public const string Ocean = "ocean";
    public const string Lava = "lava";
    public const string Fire = "fire";
    public const string Sunset = "sunset";
    public const string Neon = "neon";
    public const string Plasma = "plasma";
    public const string Comet = "comet";
    public const string Starlight = "starlight";
    public const string Storm = "storm";
    public const string Forest = "forest";
    public const string Ripple = "ripple";
    public const string Rain = "rain";
    public const string Pulse = "pulse";
    public const string Solid = "solid";

    /// <summary>Canvas size: plenty for sampling the edges.</summary>
    public const int Width = 192;
    public const int Height = 108;

    private const float Tau = 2 * MathF.PI;

    private static double? _stormThumbnailTime;

    public static IReadOnlyList<BuiltInEffect> All { get; } =
    [
        new(Rainbow, "Rainbow spin"),
        new(RainbowSweep, "Rainbow sweep"),
        new(RainbowRise, "Rainbow rise"),
        new(Spectrum, "Spectrum"),
        new(Aurora, "Aurora"),
        new(Ocean, "Ocean"),
        new(Lava, "Lava"),
        new(Fire, "Fire"),
        new(Sunset, "Sunset"),
        new(Neon, "Neon"),
        new(Plasma, "Plasma"),
        new(Comet, "Comet"),
        new(Starlight, "Starlight"),
        new(Storm, "Thunderstorm"),
        new(Forest, "Forest"),
        new(Ripple, "Ripple"),
        new(Rain, "Digital rain"),
        new(Pulse, "Pulse", UsesColor: true),
        new(Solid, "Solid color", UsesColor: true),
    ];

    public static BuiltInEffect? Find(string? id) => All.FirstOrDefault(e => e.Id == id);

    /// <summary>True when the picture changes over time (everything except a solid color).</summary>
    public static bool IsAnimated(string id) => id != Solid;

    /// <summary>A moment that shows the effect well, for gallery thumbnails.</summary>
    public static double ThumbnailTime(string id) => id switch
    {
        Pulse => 1.0, // full brightness
        Storm => _stormThumbnailTime ??= FindLightning(),
        _ => 3.0,
    };

    /// <summary>Draws effect <paramref name="id"/> at time <paramref name="t"/> (seconds) into <paramref name="frame"/>.</summary>
    public static void Render(string id, CapturedFrame frame, double t, byte r, byte g, byte b, int width = Width, int height = Height)
    {
        frame.EnsureSize(width, height);
        frame.SourceWidth = width;
        frame.SourceHeight = height;
        byte[] px = frame.Pixels;
        var color = (r / 255f, g / 255f, b / 255f);
        float time = (float)t;
        float aspect = width / (float)height;
        float flash = id == Storm ? Lightning(time, out _) : 0;

        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                (float cr, float cg, float cb) = id switch
                {
                    Rainbow => RainbowAt(u, v, time, aspect),
                    RainbowSweep => Hue(u * 0.9f - time * 0.12f),
                    RainbowRise => Hue((1 - v) * 0.9f - time * 0.12f),
                    Spectrum => Hue(time * 0.04f),
                    Aurora => AuroraAt(u, v, time),
                    Ocean => OceanAt(u, v, time),
                    Lava => LavaAt(u, v, time),
                    Fire => FireAt(u, v, time),
                    Sunset => SunsetAt(u, v, time),
                    Neon => NeonAt(u, v, time, aspect),
                    Plasma => PlasmaAt(u, v, time, aspect),
                    Comet => CometAt(u, v, time, aspect),
                    Starlight => StarlightAt(u, v, time),
                    Storm => StormAt(u, v, time, flash),
                    Forest => ForestAt(u, v, time),
                    Ripple => RippleAt(u, v, time, aspect),
                    Rain => RainAt(u, v, time),
                    Pulse => Scale(color, 0.2f + 0.8f * (0.5f + 0.5f * MathF.Sin(time * 1.6f))),
                    _ => color,
                };
                int i = (y * width + x) * 4;
                px[i] = ToByte(cb);
                px[i + 1] = ToByte(cg);
                px[i + 2] = ToByte(cr);
                px[i + 3] = 255;
            }
        }

        frame.Sequence++;
    }

    /// <summary>A rainbow turning around the centre of the screen.</summary>
    private static (float, float, float) RainbowAt(float u, float v, float t, float aspect)
    {
        float angle = MathF.Atan2(v - 0.5f, (u - 0.5f) * aspect) / Tau;
        return Hue(angle + t * 0.07f);
    }

    /// <summary>Soft green, teal and violet curtains drifting over a night sky.</summary>
    private static (float, float, float) AuroraAt(float u, float v, float t)
    {
        float wave1 = MathF.Sin(u * 5.2f + t * 0.35f + MathF.Sin(v * 3.1f - t * 0.21f) * 1.6f);
        float wave2 = MathF.Sin(u * 2.4f - t * 0.27f + v * 4.3f);
        float band = SmoothStep(-0.25f, 1f, wave1 * 0.6f + wave2 * 0.4f);
        float mix = 0.5f + 0.5f * MathF.Sin(t * 0.13f + u * 1.7f - v * 0.9f);
        (float, float, float) green = (0.08f, 0.95f, 0.55f), violet = (0.55f, 0.25f, 1f);
        (float r, float g, float b) tint = Lerp(green, violet, mix);
        return (0.02f + tint.r * band, 0.04f + tint.g * band, 0.12f + tint.b * band);
    }

    /// <summary>Deep blue water with moving light.</summary>
    private static (float, float, float) OceanAt(float u, float v, float t)
    {
        float wave = 0.5f + 0.5f * MathF.Sin(u * 6f - t * 0.8f + MathF.Sin(v * 4f + t * 0.5f) * 1.3f);
        float shimmer = MathF.Pow(0.5f + 0.5f * MathF.Sin(u * 13f + v * 9f - t * 1.7f), 6f);
        (float r, float g, float b) water = Lerp((0f, 0.07f, 0.25f), (0.02f, 0.55f, 0.85f), wave * wave);
        return (water.r + shimmer * 0.25f, water.g + shimmer * 0.35f, water.b + shimmer * 0.3f);
    }

    /// <summary>Slow, glowing red and orange blobs.</summary>
    private static (float, float, float) LavaAt(float u, float v, float t)
    {
        float n = MathF.Sin(u * 4.3f + t * 0.45f) + MathF.Sin(v * 5.1f - t * 0.31f) + MathF.Sin((u + v) * 3.2f + t * 0.67f);
        float heat = 0.5f + n / 6f;
        (float r, float g, float b) c = Lerp((0.22f, 0f, 0f), (1f, 0.32f, 0f), SmoothStep(0.15f, 0.85f, heat));
        float hot = SmoothStep(0.78f, 0.98f, heat);
        return (c.r, c.g + hot * 0.45f, c.b + hot * 0.1f);
    }

    /// <summary>Flames licking up from the bottom of the screen.</summary>
    private static (float, float, float) FireAt(float u, float v, float t)
    {
        float rise = 1 - v; // 0 at the bottom
        float n = Fbm(u * 7f, v * 4.5f + t * 1.7f);
        float heat = Math.Clamp(1.05f - rise * 1.35f + (n - 0.5f) * 1.1f, 0f, 1f);
        if (heat < 0.35f)
        {
            return Lerp((0f, 0f, 0f), (0.55f, 0.03f, 0f), heat / 0.35f);
        }

        return heat < 0.7f
            ? Lerp((0.55f, 0.03f, 0f), (1f, 0.36f, 0f), (heat - 0.35f) / 0.35f)
            : Lerp((1f, 0.36f, 0f), (1f, 0.82f, 0.32f), (heat - 0.7f) / 0.3f);
    }

    /// <summary>An evening sky: violet above, pink in the middle, orange below, gently drifting.</summary>
    private static (float, float, float) SunsetAt(float u, float v, float t)
    {
        float k = Math.Clamp(v + 0.12f * MathF.Sin(u * 2.2f + t * 0.18f), 0f, 1f);
        return k < 0.5f
            ? Lerp((0.32f, 0.08f, 0.55f), (0.95f, 0.32f, 0.5f), k * 2)
            : Lerp((0.95f, 0.32f, 0.5f), (1f, 0.58f, 0.12f), (k - 0.5f) * 2);
    }

    /// <summary>Magenta and cyan glows circling each other.</summary>
    private static (float, float, float) NeonAt(float u, float v, float t, float aspect)
    {
        float a = t * 0.45f;
        float d1 = Distance2(u, v, 0.5f + 0.32f * MathF.Cos(a), 0.5f + 0.32f * MathF.Sin(a * 1.3f), aspect);
        float d2 = Distance2(u, v, 0.5f - 0.32f * MathF.Cos(a * 0.8f), 0.5f - 0.3f * MathF.Sin(a), aspect);
        float m = MathF.Exp(-d1 * 5f), c = MathF.Exp(-d2 * 5f);
        return (0.05f + 1f * m, 0.02f + 0.9f * c, 0.12f + 0.85f * m + 1f * c);
    }

    /// <summary>Flowing bands of color, the classic demo-scene plasma.</summary>
    private static (float, float, float) PlasmaAt(float u, float v, float t, float aspect)
    {
        float x = u * aspect;
        float cx = x - aspect / 2 + 0.45f * MathF.Sin(t * 0.31f), cy = v - 0.5f + 0.35f * MathF.Cos(t * 0.23f);
        float k = MathF.Sin(x * 4.1f + t * 0.8f)
            + MathF.Sin(v * 5.3f - t * 0.6f)
            + MathF.Sin((x + v) * 3.4f + t * 0.5f)
            + MathF.Sin(MathF.Sqrt(cx * cx + cy * cy) * 7.5f - t * 0.9f);
        float phase = k * 0.125f + 0.5f + t * 0.02f;
        return (
            0.5f + 0.5f * MathF.Cos(Tau * (phase + 0.0f)),
            0.5f + 0.5f * MathF.Cos(Tau * (phase + 0.33f)),
            0.5f + 0.5f * MathF.Cos(Tau * (phase + 0.67f)));
    }

    /// <summary>A bright comet with a glowing tail circling the screen clockwise, close to its edges.</summary>
    private static (float, float, float) CometAt(float u, float v, float t, float aspect)
    {
        float around = MathF.Atan2(v - 0.5f, (u - 0.5f) * aspect) / Tau; // clockwise on screen
        float behind = Fract(t * 0.11f - around); // 0 at the head, growing along the tail
        float head = MathF.Exp(-MathF.Pow(MathF.Min(behind, 1 - behind) * 26f, 2));
        float tail = MathF.Exp(-behind * 4.5f);
        float border = SmoothStep(0.22f, 0.47f, MathF.Max(MathF.Abs(u - 0.5f), MathF.Abs(v - 0.5f)));
        (float r, float g, float b) tailColor = Lerp((0.2f, 0.75f, 1f), (0.55f, 0.15f, 0.95f), Math.Clamp(behind * 3f, 0f, 1f));
        float glow = tail * 0.85f * border, spark = head * border;
        return (
            0.01f + tailColor.r * glow + spark * 0.9f,
            0.01f + tailColor.g * glow + spark * 0.95f,
            0.04f + tailColor.b * glow + spark);
    }

    /// <summary>A night sky of softly twinkling stars.</summary>
    private static (float, float, float) StarlightAt(float u, float v, float t)
    {
        const int Columns = 24, Rows = 14;
        float gx = u * Columns, gy = v * Rows;
        int cx = (int)gx, cy = (int)gy;
        float nebula = Noise(u * 3f + t * 0.02f, v * 2.2f);
        (float r, float g, float b) sky = (0.01f + 0.04f * nebula, 0.012f + 0.02f * nebula, 0.05f + 0.07f * nebula);
        if (Hash(cx, cy) < 0.5f)
        {
            return sky;
        }

        float dx = gx - cx - (0.3f + 0.4f * Hash(cx + 3, cy + 7));
        float dy = gy - cy - (0.3f + 0.4f * Hash(cx + 11, cy + 5));
        float shape = MathF.Exp(-(dx * dx + dy * dy) * 16f);
        float speed = 0.7f + 1.5f * Hash(cx + 17, cy + 31);
        float twinkle = MathF.Pow(MathF.Max(0f, MathF.Sin(t * speed + Hash(cx + 23, cy + 2) * Tau)), 5f);
        float tint = Hash(cx + 41, cy + 13);
        (float r, float g, float b) star = tint < 0.4f ? (1f, 0.92f, 0.78f) : tint < 0.8f ? (0.75f, 0.85f, 1f) : (1f, 0.75f, 0.5f);
        float k = shape * (0.15f + 0.85f * twinkle);
        return (sky.r + star.r * k, sky.g + star.g * k, sky.b + star.b * k);
    }

    /// <summary>Dark, drifting clouds lit now and then by lightning.</summary>
    private static (float, float, float) StormAt(float u, float v, float t, float flash)
    {
        float clouds = Fbm(u * 3.2f + t * 0.06f, v * 2.3f - t * 0.02f);
        (float r, float g, float b) sky = Lerp((0.015f, 0.02f, 0.05f), (0.13f, 0.15f, 0.23f), clouds * clouds);
        if (flash <= 0)
        {
            return sky;
        }

        Lightning(t, out float centre);
        float region = MathF.Exp(-(u - centre) * (u - centre) * 9f) * (0.45f + 0.55f * (1 - v)) * (0.5f + clouds);
        float k = flash * region;
        return (sky.r + 0.85f * k, sky.g + 0.9f * k, sky.b + 1f * k);
    }

    /// <summary>Sunlight dappling through swaying leaves: deep greens with drifting patches of gold.</summary>
    private static (float, float, float) ForestAt(float u, float v, float t)
    {
        float sway = 0.12f * MathF.Sin(t * 0.5f + v * 2f);
        float leaves = Fbm(u * 4.5f + sway, v * 3.5f + t * 0.04f);
        float light = SmoothStep(0.55f, 0.82f, Fbm(u * 6f - sway * 1.5f + 3.1f, v * 5f - t * 0.07f));
        (float r, float g, float b) green = Lerp((0.01f, 0.09f, 0.03f), (0.12f, 0.5f, 0.12f), leaves);
        return (green.r + 0.95f * light, green.g + 0.78f * light, green.b + 0.3f * light);
    }

    /// <summary>Rings of color spreading out from the middle of the screen.</summary>
    private static (float, float, float) RippleAt(float u, float v, float t, float aspect)
    {
        float d = MathF.Sqrt(Distance2(u, v, 0.5f, 0.5f, aspect));
        float wave = 0.5f + 0.5f * MathF.Sin(d * 16f - t * 2.4f);
        (float r, float g, float b) c = Hue(d * 0.45f - t * 0.06f);
        return Scale(c, 0.12f + 0.88f * SmoothStep(0.25f, 1f, wave));
    }

    /// <summary>Green streaks of "digital rain" falling down the screen.</summary>
    private static (float, float, float) RainAt(float u, float v, float t)
    {
        const int Columns = 28;
        int column = (int)(u * Columns);
        float speed = 0.25f + 0.35f * Hash(column, 3);
        float head = Fract(t * speed + Hash(column, 9)) * 1.6f - 0.2f; // runs a little past the bottom
        float behind = head - v;
        if (behind < 0 || behind > 0.6f)
        {
            return (0f, 0.02f, 0.01f);
        }

        float glow = MathF.Exp(-behind * 6f);
        float flicker = 0.75f + 0.25f * Hash(column * 31 + (int)(v * 40), (int)(t * 8));
        float tip = behind < 0.03f ? 1f : 0f;
        return (0.05f + 0.6f * tip, (0.15f + 0.85f * glow) * flicker, 0.08f + 0.3f * glow + 0.5f * tip);
    }

    /// <summary>Brightness of the lightning at time <paramref name="t"/> (0 most of the time) and where across the screen it strikes.</summary>
    private static float Lightning(float t, out float centre)
    {
        const float Slot = 0.9f;
        int slot = (int)MathF.Floor(t / Slot);
        centre = 0.15f + 0.7f * Hash(slot, 29);
        if (Hash(slot, 7) > 0.26f)
        {
            return 0;
        }

        float since = t - (slot * Slot + Hash(slot, 13) * 0.5f);
        if (since < 0 || since > 0.4f)
        {
            return 0;
        }

        float flicker = since is > 0.09f and < 0.15f ? 0.7f : 0f; // the second flash of a strike
        return Math.Min(1f, MathF.Exp(-since * 11f) + flicker);
    }

    private static double FindLightning()
    {
        for (double t = 2; t < 60; t += 0.02)
        {
            if (Lightning((float)t, out _) > 0.8f)
            {
                return t + 0.03;
            }
        }

        return 3.0;
    }

    private static float Distance2(float u, float v, float cu, float cv, float aspect)
    {
        float du = (u - cu) * aspect, dv = v - cv;
        return du * du + dv * dv;
    }

    private static (float, float, float) Hue(float h)
    {
        h -= MathF.Floor(h);
        float r = Math.Clamp(MathF.Abs(h * 6 - 3) - 1, 0, 1);
        float g = Math.Clamp(2 - MathF.Abs(h * 6 - 2), 0, 1);
        float b = Math.Clamp(2 - MathF.Abs(h * 6 - 4), 0, 1);
        return (r, g, b);
    }

    /// <summary>Repeatable pseudo-random value in [0, 1] for a lattice point.</summary>
    private static float Hash(int x, int y)
    {
        uint h = unchecked((uint)(x * 374761393 + y * 668265263));
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
    }

    /// <summary>Smooth value noise in [0, 1].</summary>
    private static float Noise(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        float sx = xf * xf * (3 - 2 * xf), sy = yf * yf * (3 - 2 * yf);
        float top = Hash(xi, yi) + (Hash(xi + 1, yi) - Hash(xi, yi)) * sx;
        float bottom = Hash(xi, yi + 1) + (Hash(xi + 1, yi + 1) - Hash(xi, yi + 1)) * sx;
        return top + (bottom - top) * sy;
    }

    /// <summary>Two octaves of <see cref="Noise"/>.</summary>
    private static float Fbm(float x, float y) => Noise(x, y) * 0.65f + Noise(x * 2.1f + 5.2f, y * 2.1f + 1.3f) * 0.35f;

    private static float Fract(float x) => x - MathF.Floor(x);

    private static (float, float, float) Lerp((float r, float g, float b) a, (float r, float g, float b) b, float k) =>
        (a.r + (b.r - a.r) * k, a.g + (b.g - a.g) * k, a.b + (b.b - a.b) * k);

    private static (float, float, float) Scale((float r, float g, float b) c, float k) => (c.r * k, c.g * k, c.b * k);

    private static float SmoothStep(float e0, float e1, float x)
    {
        float k = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return k * k * (3 - 2 * k);
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
}
