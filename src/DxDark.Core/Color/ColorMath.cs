using System.Numerics;

namespace DxDark.Core.Color;

/// <summary>sRGB transfer functions, luma and color-temperature helpers. Colors are Vector3(R, G, B).</summary>
public static class ColorMath
{
    public static readonly Vector3 Luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>sRGB byte → linear light, 0..1.</summary>
    public static readonly float[] SrgbByteToLinear = BuildDecodeTable();

    /// <summary>sRGB byte → linear light scaled to 0..65535 (for integer integral images).</summary>
    public static readonly ushort[] SrgbByteToLinear16 = SrgbByteToLinear.Select(v => (ushort)Math.Round(v * 65535)).ToArray();

    public static float Decode(float encoded)
    {
        encoded = Math.Clamp(encoded, 0f, 1f);
        return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    public static float Encode(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    public static Vector3 Decode(Vector3 c) => new(Decode(c.X), Decode(c.Y), Decode(c.Z));

    public static Vector3 Encode(Vector3 c) => new(Encode(c.X), Encode(c.Y), Encode(c.Z));

    public static float MaxComponent(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z));

    public static float MinComponent(Vector3 c) => MathF.Min(c.X, MathF.Min(c.Y, c.Z));

    /// <summary>
    /// Per-channel multipliers that tint white towards the given color temperature, normalized
    /// so 6500 K is neutral (1, 1, 1) and no channel exceeds 1.
    /// </summary>
    public static Vector3 TemperatureGains(double kelvin)
    {
        Vector3 target = BlackBody(kelvin);
        Vector3 neutral = BlackBody(6500);
        var gains = new Vector3(target.X / neutral.X, target.Y / neutral.Y, target.Z / neutral.Z);
        return gains / MaxComponent(gains);
    }

    /// <summary>Approximate sRGB color of a black body (Tanner Helland's fit), 0..1.</summary>
    private static Vector3 BlackBody(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100.0;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return new Vector3(
            (float)Math.Clamp(r, 1, 255) / 255f,
            (float)Math.Clamp(g, 1, 255) / 255f,
            (float)Math.Clamp(b, 1, 255) / 255f);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static bool TryParseHex(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int value))
        {
            return false;
        }

        r = (byte)(value >> 16);
        g = (byte)(value >> 8);
        b = (byte)value;
        return true;
    }

    private static float[] BuildDecodeTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = Decode(i / 255f);
        }

        return table;
    }
}
