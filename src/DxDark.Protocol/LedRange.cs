namespace DxDark.Protocol;

/// <summary>
/// One color applied to an inclusive range of LEDs. LED numbers are 1-based, as the controller
/// expects; on the wire a range is the five bytes [first, red, green, blue, last].
/// </summary>
public readonly record struct LedRange(int First, int Last, byte R, byte G, byte B)
{
    /// <summary>Highest LED number the protocol can address.</summary>
    public const int MaxLed = 254;

    public static LedRange Single(int led, byte r, byte g, byte b) => new(led, led, r, g, b);

    internal void WriteTo(Span<byte> destination)
    {
        if (First < 1 || Last < First || Last > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(First), $"Invalid LED range {First}–{Last}.");
        }

        destination[0] = (byte)First;
        destination[1] = R;
        destination[2] = G;
        destination[3] = B;
        destination[4] = (byte)Last;
    }
}
